using System.Threading.Channels;
using SerialBridge.Core.Capture;
using SerialBridge.Core.Logging;
using SerialBridge.Core.Models;
using SerialBridge.Core.Net;
using SerialBridge.Core.Serial;
using SerialBridge.Core.Tcp;

namespace SerialBridge.Core.Bridge;

/// <summary>一条映射的实时运行状态快照（供 UI 绑定轮询/事件刷新）。</summary>
public sealed record MappingStatus(
    MappingState State,
    SerialState SerialState,
    string? SerialMessage,
    int ClientCount,
    long SerialRxBytes,
    long SerialTxBytes);

/// <summary>监控条目：一条带方向与时间戳的数据记录。</summary>
public sealed record MonitorEntry(DateTime TimeUtc, DataDirection Direction, byte[] Data);

/// <summary>
/// 映射运行时：把物理串口与 TCP 服务端组合起来，实现双向透明转发。
/// 串口 → 网络：串口读循环回调中复制数据、更新统计、广播给客户端。
/// 网络 → 串口：有界队列提供异步背压，由单一写任务按顺序写入串口。
/// 每条映射完全独立，任何一方的异常只影响本映射。
/// </summary>
public sealed class MappingRuntime : IAsyncDisposable
{
    private readonly SerialPortDevice _serial;
    private readonly TcpServerService _tcp;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private volatile bool _acceptingWrites;

    private long _serialRxBytes;   // 串口收到（→ 发往网络）
    private long _serialTxBytes;   // 串口发出（← 来自网络）
    private int _clientCount;

    private volatile MappingState _state = MappingState.Stopped;
    private string? _faultMessage;

    public MappingConfig Config { get; }

    public MappingState State => _state;
    public string? FaultMessage => _faultMessage;
    public SerialPortDevice Serial => _serial;
    public TcpServerService Tcp => _tcp;
    public long SerialRxBytes => Interlocked.Read(ref _serialRxBytes);
    public long SerialTxBytes => Interlocked.Read(ref _serialTxBytes);
    public int ClientCount => Volatile.Read(ref _clientCount);

    public bool IsRunning => _state is MappingState.Running or MappingState.Starting;
    public long TotalAcceptedClients => _tcp.TotalAccepted;
    public SerialCaptureService? Capture { get; private set; }

    /// <summary>状态变化（后台线程触发）。</summary>
    public event Action<MappingRuntime>? StatusChanged;

    /// <summary>数据监控事件（后台线程触发；payload 为只读副本）。</summary>
    public event Action<MonitorEntry>? DataTransferred;

    public MappingRuntime(MappingConfig config, IPortStreamFactory? factory = null)
    {
        Config = config;
        _serial = new SerialPortDevice(factory ?? new SerialPortStreamFactory(),
            new PortParams(config.PortName, config.BaudRate, config.DataBits,
                config.Parity, config.StopBits, config.FlowControl));
        _tcp = new TcpServerService(config);

        _serial.DataReceived += OnSerialDataReceived;
        _serial.StateChanged += OnSerialStateChanged;
        _tcp.ReceiveFromClientAsync = OnClientDataAsync;
        _tcp.ClientConnected += OnClientConnected;
        _tcp.ClientClosed += OnClientClosed;
    }

    // ---------- 生命周期 ----------

    /// <summary>
    /// 启动映射：先起 TCP 监听（失败则整体失败），再启动串口会话。
    /// 串口暂时打不开不影响映射启动（TCP 侧照常监听，串口侧重连/报错）。
    /// </summary>
    public async Task StartAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_state is MappingState.Running or MappingState.Starting) return;
            if (_serialWriterTask is { IsCompleted: false })
            {
                _state = MappingState.Faulted;
                _faultMessage = "上次串口写入尚未结束，请等待设备释放后重试。";
                StatusChanged?.Invoke(this);
                return;
            }
            _serialWriteQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2048)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
            _writerCts?.Dispose();
            _writerCts = new CancellationTokenSource();
            _state = MappingState.Starting;
            _faultMessage = null;
            StatusChanged?.Invoke(this);
            _acceptingWrites = true;
            try
            {
                _tcp.Start();
            }
            catch (Exception ex)
            {
                _acceptingWrites = false;
                _serialWriteQueue.Writer.TryComplete();
                _serialWriteQueue = null;
                _writerCts.Dispose();
                _writerCts = null;
                _state = MappingState.Faulted;
                _faultMessage = ex.Message;
                StatusChanged?.Invoke(this);
                LogX.Error($"映射「{Config.Name}」启动失败：{ex.Message}", ex);
                return;
            }
            _serial.Configure(
                new PortParams(Config.PortName, Config.BaudRate, Config.DataBits,
                    Config.Parity, Config.StopBits, Config.FlowControl),
                Config.AutoReconnect, Config.ReconnectIntervalMs);
            _serial.Start();
            var queue = _serialWriteQueue;
            var token = _writerCts.Token;
            _serialWriterTask = Task.Run(() => SerialWriterLoopAsync(queue, token));

            if (Config.DataLog.Enabled && !string.IsNullOrWhiteSpace(Config.DataLog.Directory))
            {
                var capture = new SerialCaptureService(new SerialCaptureOptions
                {
                    Directory = Config.DataLog.Directory,
                    MappingName = Config.Name,
                    Format = Config.DataLog.Format,
                    TextEncoding = Config.DataLog.TextEncoding,
                    RetentionDays = Config.DataLog.RetentionDays,
                    MaxFileBytes = (long)Config.DataLog.MaxFileMB * 1024 * 1024,
                });
                capture.Start();
                Capture = capture;
                capture.StateChanged += () => { try { StatusChanged?.Invoke(this); } catch { } };
                LogX.Info($"映射「{Config.Name}」串口日志已启用：{Config.DataLog.Directory}");
            }
            _state = MappingState.Running;
            StatusChanged?.Invoke(this);
            LogX.Info($"映射「{Config.Name}」已启动：{Config.PortName} ↔ {Config.ListenIp}:{Config.ListenPort}");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_state == MappingState.Stopped) return;
            _acceptingWrites = false;
            _writerCts?.Cancel();
            _serialWriteQueue?.Writer.TryComplete();
            _serialWriteQueue = null;

            // Close producers first. Closing the serial session also releases an in-flight physical Write.
            await _tcp.StopAsync().ConfigureAwait(false);
            await _serial.StopAsync().ConfigureAwait(false);
            Capture?.Dispose();
            Capture = null;
            if (_serialWriterTask is not null)
            {
                try { await _serialWriterTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    _state = MappingState.Faulted;
                    _faultMessage = "串口写入未能停止，设备释放前禁止重新启动。";
                    StatusChanged?.Invoke(this);
                    return;
                }
                _serialWriterTask = null;
            }
            _writerCts?.Dispose();
            _writerCts = null;
            _state = MappingState.Stopped;
            _faultMessage = null;
            Volatile.Write(ref _clientCount, 0);
            StatusChanged?.Invoke(this);
            LogX.Info($"映射「{Config.Name}」已停止");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    // ---------- 数据路径 ----------

    // 网络 → 串口写队列：所有客户端的数据按 TCP 到达顺序经单一写任务落串口，
    // 消除并发 Write 的乱序风险（需求：保证各方向的数据顺序）。
    private Channel<byte[]>? _serialWriteQueue;
    private CancellationTokenSource? _writerCts;
    private Task? _serialWriterTask;

    private void OnSerialDataReceived(byte[] data)
    {
        Interlocked.Add(ref _serialRxBytes, data.Length);
        Publish(DataDirection.SerialToNet, data);
        Capture?.Enqueue(DataDirection.SerialToNet, data);
        _tcp.Broadcast(data);
    }

    private async ValueTask OnClientDataAsync(TcpClientConnection conn, byte[] data, CancellationToken ct)
    {
        var queue = _serialWriteQueue;
        if (!_acceptingWrites || queue is null) return;
        try
        {
            // Awaiting here pauses this client's TCP reads, letting TCP apply backpressure without losing bytes.
            await queue.Writer.WriteAsync(data, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (ChannelClosedException) { return; }
        if (!_acceptingWrites || !ReferenceEquals(queue, _serialWriteQueue)) return;
        Interlocked.Add(ref _serialTxBytes, data.Length);
        Publish(DataDirection.NetToSerial, data);
        Capture?.Enqueue(DataDirection.NetToSerial, data);
    }

    /// <summary>
    /// 本地（软件界面）直接向设备写入数据（内置串口终端使用）。
    /// 与 TCP 客户端数据共享同一条 FIFO 写队列，语义与"一个特权本地客户端"一致：
    /// 不受 TCP 客户端数量限制，计入收发统计与监控。
    /// </summary>
    public bool WriteLocal(byte[] data)
    {
        var queue = _serialWriteQueue;
        if (!IsRunning || !_acceptingWrites || queue is null || !queue.Writer.TryWrite(data)) return false;
        Interlocked.Add(ref _serialTxBytes, data.Length);
        Publish(DataDirection.NetToSerial, data);
        Capture?.Enqueue(DataDirection.NetToSerial, data);
        return true;
    }

    /// <summary>串口写任务：按到达顺序逐块写入（SendAsync 内部保证单次 Write 原子）。</summary>
    private async Task SerialWriterLoopAsync(Channel<byte[]> queue, CancellationToken ct)
    {
        try
        {
            await foreach (var data in queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                var ok = await _serial.SendAsync(data, ct).ConfigureAwait(false);
                if (!ok && _serial.IsOpen)
                    LogX.Warn($"映射「{Config.Name}」有 {data.Length} 字节未能写入串口。");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogX.Error($"映射「{Config.Name}」串口写任务异常终止：{ex.Message}", ex);
        }
    }

    private void OnClientConnected(TcpClientConnection conn)
    {
        Volatile.Write(ref _clientCount, _tcp.ClientCount);
        StatusChanged?.Invoke(this);
    }

    private void OnClientClosed(TcpClientConnection conn, string reason)
    {
        Volatile.Write(ref _clientCount, _tcp.ClientCount);
        StatusChanged?.Invoke(this);
    }

    private void OnSerialStateChanged(SerialState state, string? message) => StatusChanged?.Invoke(this);

    private void Publish(DataDirection dir, byte[] data)
    {
        var handler = DataTransferred;
        if (handler is null) return;
        try { handler(new MonitorEntry(DateTime.UtcNow, dir, data)); }
        catch (Exception ex) { LogX.Error("监控数据处理异常：" + ex.Message, ex); }
    }

    /// <summary>运行时状态快照。</summary>
    public MappingStatus GetStatus() => new(
        _state, _serial.State, _serial.StateMessage,
        ClientCount, SerialRxBytes, SerialTxBytes);

    /// <summary>客户端应使用的访问地址描述（不含 0.0.0.0）。</summary>
    public string AccessAddress => NetworkInfo.DescribeAccessAddresses(Config.ListenIp, Config.ListenPort);

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _serial.DisposeAsync();
        await _tcp.DisposeAsync();
    }
}
