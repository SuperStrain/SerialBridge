using System.IO;
using SerialBridge.Core.Logging;
using SerialBridge.Core.Models;

namespace SerialBridge.Core.Serial;

/// <summary>
/// 物理串口会话管理：负责打开串口、后台读取循环、写入串行化，
/// 以及意外断开（含 USB 热插拔）后的自动重连。
/// 同一时刻只会话循环一个任务；读循环通过 ReadTimeout 轮询实现可中断退出。
/// </summary>
public sealed class SerialPortDevice : IAsyncDisposable
{
    private readonly IPortStreamFactory _factory;
    private readonly object _gate = new();

    private PortParams _params;
    private bool _autoReconnect = true;
    private int _reconnectIntervalMs = 3000;

    private CancellationTokenSource? _cts;
    private Task? _sessionTask;

    /// <summary>当前打开的流；仅在会话循环内赋值/清空，读方取快照使用。</summary>
    private volatile PortStreamHandle? _current;

    private volatile SerialState _state = SerialState.Stopped;
    private string? _stateMessage;

    /// <summary>状态变化通知。可能在后台线程触发。</summary>
    public event Action<SerialState, string?>? StateChanged;

    /// <summary>串口收到原始数据（已复制独立缓冲）。可能在后台线程触发。</summary>
    public event Action<byte[]>? DataReceived;

    public SerialPortDevice(IPortStreamFactory factory, PortParams initialParams)
    {
        _factory = factory;
        _params = initialParams;
    }

    public SerialState State => _state;
    public string? StateMessage => _stateMessage;
    public bool IsOpen => State == SerialState.Open;
    public string PortName => _params.PortName;

    /// <summary>运行中更新参数（如重连间隔、自动重连开关）。</summary>
    public void Configure(PortParams parameters, bool autoReconnect, int reconnectIntervalMs)
    {
        lock (_gate)
        {
            _params = parameters;
            _autoReconnect = autoReconnect;
            _reconnectIntervalMs = Math.Clamp(reconnectIntervalMs, 500, 60_000);
        }
    }

    /// <summary>启动串口会话（幂等：已启动则忽略）。</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_sessionTask is { IsCompleted: false }) return;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _sessionTask = Task.Run(() => SessionLoopAsync(ct), ct);
        }
    }

    /// <summary>停止会话并释放串口（幂等）。</summary>
    public async Task StopAsync()
    {
        Task? task;
        lock (_gate)
        {
            _cts?.Cancel();
            task = _sessionTask;
        }
        if (task is not null)
        {
            try
            {
                // 读循环最长一个 ReadTimeout（200ms）后退出
                await task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch { /* 超时/取消均按已停止处理 */ }
        }
        SetState(SerialState.Stopped, null);
    }

    /// <summary>
    /// 向串口写入数据。由调用方保证顺序语义（串口写用信号量串行化）。
    /// 返回 false 表示串口当前不可用（未打开或已断开）。
    /// </summary>
    public async Task<bool> SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var stream = _current?.Stream;
        if (stream is null) return false;
        try
        {
            var buffer = data.ToArray();
            // 与读路径一致：同步 Write 由线程池执行，WriteTimeout 兜底防永久阻塞
            await Task.Run(() => stream.Write(buffer, 0, buffer.Length), CancellationToken.None);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            LogX.Error($"写入串口 {_params.PortName} 失败：{ex.Message}", ex);
            return false;
        }
    }

    // ---------- 内部实现 ----------

    private async Task SessionLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            PortStreamHandle? handle = null;
            var opened = false;
            PortParams p;
            bool autoReconnect;
            int interval;
            lock (_gate)
            {
                p = _params;
                autoReconnect = _autoReconnect;
                interval = _reconnectIntervalMs;
            }
            try
            {
                SetState(SerialState.Opening, null);

                handle = _factory.Open(p);
                _current = handle;
                opened = true;
                SetState(SerialState.Open, null);

                await ReadLoopAsync(handle.Stream, ct);
                // ReadLoop 正常返回 = 收到取消
                break;
            }
            catch (PortOpenException pex)
            {
                _current = null;
                if (pex.Kind == PortOpenError.AccessDenied)
                {
                    SetState(SerialState.Faulted, "串口被其他程序占用");
                    LogX.Error(pex.Message);
                    break; // 被占用不自动重试，等待用户干预
                }
                LogX.Warn(pex.Message);
                if (!await WaitReconnectAsync(autoReconnect, interval, ct)) break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _current = null;
                LogX.Error($"串口 {_params.PortName} 会话异常：{ex.Message}", ex);
                SetState(SerialState.ReconnectWaiting, "串口异常断开");
                if (!await WaitReconnectAsync(autoReconnect, interval, ct)) break;
            }
            finally
            {
                if (!opened) SetStateAfterFailure();
                handle?.Dispose();
                if (_current == handle) _current = null;
            }
        }
    }

    private async Task ReadLoopAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (true)
        {
            int n;
            try
            {
                // 同步 Read 由线程池执行：SerialPort.Read 按 ReadTimeout 抛 TimeoutException，
                // 是稳定可控的读方式（BaseStream.ReadAsync 在超时时抛 IOException 而非超时异常）
                n = await Task.Run(() => stream.Read(buffer, 0, buffer.Length), CancellationToken.None);
            }
            catch (TimeoutException)
            {
                ct.ThrowIfCancellationRequested();
                continue;
            }
            catch (IOException ex)
            {
                throw new PortBrokenException("串口数据读取失败（设备可能已被移除）", ex);
            }
            catch (ObjectDisposedException ex)
            {
                throw new PortBrokenException("串口已被关闭", ex);
            }
            ct.ThrowIfCancellationRequested();
            if (n <= 0) continue;
            var chunk = buffer.AsSpan(0, n).ToArray();
            try
            {
                DataReceived?.Invoke(chunk);
            }
            catch (Exception ex)
            {
                LogX.Error("串口数据回调处理异常：" + ex.Message, ex);
            }
        }
    }

    /// <summary>等待重连间隔；返回 false 表示不应继续重连（已取消或未启用自动重连）。</summary>
    private async Task<bool> WaitReconnectAsync(bool autoReconnect, int interval, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!autoReconnect)
        {
            SetState(SerialState.Faulted, "串口连接失败");
            return false;
        }
        SetState(SerialState.ReconnectWaiting, $"将在 {interval / 1000.0:0.#} 秒后重连");
        try
        {
            await Task.Delay(interval, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void SetStateAfterFailure()
    {
        // 打开失败且不再重试时由各 catch 分支设置终态；此处兜底恢复 Stopped
        if (State == SerialState.Opening) SetState(SerialState.Faulted, "串口打开失败");
    }

    private void SetState(SerialState state, string? message)
    {
        _state = state;
        _stateMessage = message;
        try
        {
            StateChanged?.Invoke(state, message);
        }
        catch (Exception ex)
        {
            LogX.Error("串口状态回调异常：" + ex.Message, ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
    }
}

/// <summary>会话运行中串口意外断开（读/写失败）的内部标记异常。</summary>
internal sealed class PortBrokenException(string message, Exception inner)
    : Exception(message, inner);
