using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using SerialBridge.Core.Logging;
using SerialBridge.Core.Models;

namespace SerialBridge.Core.Tcp;

/// <summary>
/// 一条已建立的 TCP 客户端连接。
/// 每个连接拥有独立的发送队列（Channel）：串口数据先入队，由独立任务写出，
/// 保证慢速客户端不会阻塞串口读取或拖垮其他客户端；
/// 队列积压超限时主动断开该客户端（慢速客户端保护）。
/// </summary>
public sealed class TcpClientConnection : IDisposable
{
    /// <summary>发送队列最多缓存的消息条数（每条最大约 64KB，总上限约 16MB）。</summary>
    private const int SendQueueCapacity = 256;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly Channel<byte[]> _outbox;
    private readonly CancellationTokenSource _cts = new();

    private Task? _recvTask;
    private Task? _sendTask;

    private long _sentBytes;

    public Guid Id { get; } = Guid.NewGuid();
    public string RemoteEndPoint { get; }
    public DateTime ConnectedAtUtc { get; } = DateTime.UtcNow;
    public long SentBytes => Interlocked.Read(ref _sentBytes);

    /// <summary>该客户端发来的数据（网络 → 串口方向）。后台线程触发。</summary>
    public event Action<TcpClientConnection, byte[]>? DataReceived;

    /// <summary>连接结束（主动断开、异常或被服务端踢出）。后台线程触发。</summary>
    public event Action<TcpClientConnection, string>? Closed;

    internal TcpClientConnection(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
        RemoteEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "未知";
        _outbox = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(SendQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    internal void Start()
    {
        _recvTask = Task.Run(() => RecvLoopAsync(_cts.Token));
        _sendTask = Task.Run(() => SendLoopAsync(_cts.Token));
    }

    /// <summary>将串口数据排入该客户端的发送队列。队列已满（慢速客户端）时断开连接。</summary>
    public bool EnqueueSend(byte[] data)
    {
        if (_cts.IsCancellationRequested) return false;
        if (_outbox.Writer.TryWrite(data)) return true;
        LogX.Warn($"客户端 {RemoteEndPoint} 发送队列积压超限（疑似慢速客户端），主动断开。");
        Kick("发送缓冲区积压超限");
        return false;
    }

    /// <summary>服务端主动断开该客户端。</summary>
    public void Kick(string reason)
    {
        try
        {
            _cts.Cancel();
            _client.Close();
        }
        catch { /* 关闭时的异常忽略 */ }
        Closed?.Invoke(this, reason);
    }

    private async Task RecvLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await _stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (n <= 0)
                {
                    Closed?.Invoke(this, "客户端主动断开");
                    return;
                }
                var chunk = buffer.AsSpan(0, n).ToArray();
                DataReceived?.Invoke(this, chunk);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException)
        {
            Closed?.Invoke(this, "连接异常断开");
        }
        catch (ObjectDisposedException)
        {
            Closed?.Invoke(this, "连接已关闭");
        }
        catch (SocketException ex)
        {
            Closed?.Invoke(this, $"网络错误：{ex.SocketErrorCode}");
        }
        catch (Exception ex)
        {
            LogX.Error($"客户端 {RemoteEndPoint} 接收异常：{ex.Message}", ex);
            Closed?.Invoke(this, "接收异常");
        }
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var data in _outbox.Reader.ReadAllAsync(ct))
            {
                await _stream.WriteAsync(data, ct);
                Interlocked.Add(ref _sentBytes, data.Length);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { /* 对端断开，RecvLoop 会报告关闭原因 */ }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
        catch (Exception ex)
        {
            LogX.Error($"客户端 {RemoteEndPoint} 发送异常：{ex.Message}", ex);
        }
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
            _client.Close();
            _cts.Dispose();
        }
        catch { }
    }
}

/// <summary>
/// 单条映射的 TCP 服务端：监听指定地址与端口，管理客户端接入
/// （单客户端模式拒绝后来者 / 多客户端模式限流），并向外广播串口数据。
/// </summary>
public sealed class TcpServerService : IAsyncDisposable
{
    private readonly MappingConfig _config;
    private readonly ConcurrentDictionary<Guid, TcpClientConnection> _clients = new();
    private readonly CancellationTokenSource _cts = new();

    private TcpListener? _listener;
    private Task? _acceptTask;
    private volatile bool _listening;

    public bool IsListening => _listening;
    public int ClientCount => _clients.Count;
    public IEnumerable<TcpClientConnection> Clients => _clients.Values;
    public int ListenPort => _config.ListenPort;

    /// <summary>新客户端接入（已通过数量限制检查）。后台线程触发。</summary>
    public event Action<TcpClientConnection>? ClientConnected;

    /// <summary>客户端断开。后台线程触发。</summary>
    public event Action<TcpClientConnection, string>? ClientClosed;

    /// <summary>新连接因达到上限被拒绝。后台线程触发。</summary>
    public event Action<string, string>? ClientRejected;

    /// <summary>某客户端发来数据（网络 → 串口方向）。后台线程触发。</summary>
    public event Action<TcpClientConnection, byte[]>? DataFromClient;

    public TcpServerService(MappingConfig config) => _config = config;

    /// <summary>开始监听。端口被占用或无权限时抛异常。</summary>
    public void Start()
    {
        if (_listening) return;
        var ip = _config.ListenIp == "0.0.0.0"
            ? IPAddress.Any
            : IPAddress.Parse(_config.ListenIp);
        _listener = new TcpListener(ip, _config.ListenPort);
        try
        {
            _listener.Start();
        }
        catch (SocketException ex)
        {
            _listener = null;
            if (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
                throw new IOException($"TCP 端口 {_config.ListenPort} 已被其他程序占用", ex);
            throw new IOException($"TCP 监听失败：{ex.Message}（{ex.SocketErrorCode}）", ex);
        }
        _listening = true;
        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        LogX.Info($"TCP 监听已启动：{_config.ListenIp}:{_config.ListenPort}（模式：{(_config.ClientMode == ClientMode.Single ? "单客户端" : $"多客户端，上限 {MaxClients()}")}）");
    }

    private int MaxClients() =>
        _config.ClientMode == ClientMode.Single ? 1 : Math.Max(1, _config.MaxClients);

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener!;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient tcp;
                try
                {
                    tcp = await listener.AcceptTcpClientAsync(ct);
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted) { break; }
                catch (ObjectDisposedException) { break; }
                catch (IOException)
                {
                    // 偶发的 accept 失败不终止监听
                    await Task.Delay(100, ct);
                    continue;
                }

                tcp.NoDelay = true;
                var remote = tcp.Client.RemoteEndPoint?.ToString() ?? "未知";
                var max = MaxClients();
                if (_clients.Count >= max)
                {
                    LogX.Warn($"拒绝来自 {remote} 的新连接：已达客户端上限（{max}）。");
                    ClientRejected?.Invoke(remote,
                        _config.ClientMode == ClientMode.Single ? "单客户端模式下已有客户端接入" : $"已达到最大客户端数量（{max}）");
                    try { tcp.Close(); } catch { }
                    continue;
                }

                var conn = new TcpClientConnection(tcp);
                conn.DataReceived += (c, data) =>
                {
                    try { DataFromClient?.Invoke(c, data); }
                    catch (Exception ex) { LogX.Error("客户端数据处理异常：" + ex.Message, ex); }
                };
                conn.Closed += OnConnectionClosed;
                _clients[conn.Id] = conn;
                conn.Start();
                LogX.Info($"客户端已连接：{remote}（当前 {_clients.Count}/{max}）");
                ClientConnected?.Invoke(conn);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogX.Error($"TCP 接受循环异常终止：{ex.Message}", ex);
        }
    }

    private void OnConnectionClosed(TcpClientConnection conn, string reason)
    {
        if (_clients.TryRemove(conn.Id, out _))
        {
            LogX.Info($"客户端已断开：{conn.RemoteEndPoint}（{reason}，当前 {_clients.Count} 个连接）");
            ClientClosed?.Invoke(conn, reason);
        }
        conn.Dispose();
    }

    /// <summary>将串口收到的数据广播给所有客户端（多客户端）或发送给唯一客户端（单客户端）。</summary>
    public void Broadcast(byte[] data)
    {
        foreach (var conn in _clients.Values)
            conn.EnqueueSend(data);
    }

    public async Task StopAsync()
    {
        if (!_listening) return;
        _listening = false;
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        foreach (var conn in _clients.Values)
            conn.Kick("服务端停止监听");
        _clients.Clear();
        if (_acceptTask is not null)
        {
            try { await _acceptTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
        LogX.Info($"TCP 监听已停止：{_config.ListenIp}:{_config.ListenPort}");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
    }
}
