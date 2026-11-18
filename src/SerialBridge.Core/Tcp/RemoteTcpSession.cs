using System.Diagnostics;
using System.IO;
using SerialBridge.Core.Logging;
using System.Net.Sockets;
using System.Threading.Channels;
using SerialBridge.Core.Models;

namespace SerialBridge.Core.Tcp;

/// <summary>
/// One outgoing raw TCP stream. The caller owns the session; terminal windows
/// only subscribe to its events. Bytes are NEVER replayed across reconnects.
/// </summary>
public sealed class RemoteTcpSession : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _cancel;
    private TcpClient? _client;
    private Channel<byte[]>? _output;
    private Task? _worker;
    private volatile bool _connected;
    private long _sent, _received;
    private bool _disposed;
    private int _generation;
    private string _lastDisconnectReason = "未连接";
    private bool _quickDisconnect;

    public RemoteConnectionConfig Config { get; }
    public bool IsConnected => _connected;
    public string LastDisconnectReason => _lastDisconnectReason;
    public bool WasQuickDisconnect => _quickDisconnect;
    public long SentBytes => Interlocked.Read(ref _sent);
    public long ReceivedBytes => Interlocked.Read(ref _received);
    public event Action<byte[]>? DataReceived;
    public event Action? StatusChanged;
    public event Action<string>? ConnectionLost;

    public RemoteTcpSession(RemoteConnectionConfig config) => Config = config;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connected) return;
            // A previous read loop can be complete while its CTS/socket/queue
            // still exist. Retire it BEFORE allowing a new connection.
            await StopCoreAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var client = new TcpClient();
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(8));
                await client.ConnectAsync(Config.Host, Config.Port, deadline.Token).ConfigureAwait(false);
                client.NoDelay = true;
                var cancel = new CancellationTokenSource();
                var output = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    // Never claim success for a dropped serial command.
                    FullMode = BoundedChannelFullMode.Wait,
                });
                _client = client;
                _cancel = cancel;
                _output = output;
                _connected = true;
                _lastDisconnectReason = "";
                _quickDisconnect = false;
                var connectedAt = Stopwatch.GetTimestamp();
                var generation = ++_generation;
                _worker = RunAsync(client, output, cancel.Token, generation, connectedAt);
                NotifyStatus();
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>True means queued; false means disconnected or queue full.</summary>
    public bool Send(byte[] data)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));
        if (data.Length == 0) return _connected;
        var output = _output;
        return _connected && output is not null && output.Writer.TryWrite(data.ToArray());
    }

    private async Task RunAsync(TcpClient client, Channel<byte[]> output,
        CancellationToken token, int generation, long connectedAt)
    {
        var peerClosedWithoutData = false;
        var reason = "远程连接已断开";
        try
        {
            var stream = client.GetStream();
            var writer = WriteAsync(stream, output.Reader, token);
            var buffer = new byte[8192];
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var n = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (n == 0)
                    {
                        peerClosedWithoutData = true;
                        break;
                    }
                    Interlocked.Add(ref _received, n);
                    // Subscribers are UI consumers and must not kill the TCP loop.
                    try { DataReceived?.Invoke(buffer.AsSpan(0, n).ToArray()); }
                    catch (Exception) { /* isolate terminal observers */ }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                reason = "远程连接异常：" + ex.Message;
            }
            finally
            {
                output.Writer.TryComplete();
                client.Dispose();
                try { await writer.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                {
                    reason = "远程发送失败：" + ex.Message;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            reason = "远程连接异常：" + ex.Message;
        }
        finally
        {
            // Never clear a newer session or report a user-initiated disconnect as lost.
            if (generation == Volatile.Read(ref _generation))
            {
                // A quick EOF may indicate a server-side limit, but raw TCP
                // carries no rejection reason. Never claim the cause is certain.
                var elapsed = Stopwatch.GetElapsedTime(connectedAt);
                _quickDisconnect = peerClosedWithoutData &&
                    elapsed < TimeSpan.FromSeconds(1) &&
                    Interlocked.Read(ref _received) == 0;
                if (_quickDisconnect)
                    reason = "连接后立即断开，可能已达服务端连接数上限";
                _connected = false;
                _lastDisconnectReason = reason;
                LogX.Info($"远程 TCP {Config.Host}:{Config.Port} 已断开（持续 {elapsed.TotalMilliseconds:F0} ms；{reason}）");
                NotifyStatus();
                try { ConnectionLost?.Invoke(reason); }
                catch (Exception) { /* observer failures must not fault worker */ }
            }
        }
    }

    private async Task WriteAsync(NetworkStream stream, ChannelReader<byte[]> reader,
        CancellationToken token)
    {
        await foreach (var bytes in reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            await stream.WriteAsync(bytes, token).ConfigureAwait(false);
            Interlocked.Add(ref _sent, bytes.Length);
        }
    }

    private void NotifyStatus()
    {
        try { StatusChanged?.Invoke(); }
        catch (Exception) { /* session must survive bad observers */ }
    }

    /// <summary>Must be called with _lifecycle held. Safe after natural EOF.</summary>
    private async Task StopCoreAsync()
    {
        if (_client is null && _worker is null && _cancel is null) return;
        _connected = false;
        ++_generation;
        var cancel = _cancel;
        var client = _client;
        var output = _output;
        var worker = _worker;
        _cancel = null;
        _client = null;
        _output = null;
        _worker = null;
        cancel?.Cancel();
        output?.Writer.TryComplete();
        client?.Dispose();
        try
        {
            if (worker is not null) await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally { cancel?.Dispose(); }
        NotifyStatus();
    }

    public async Task DisconnectAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
        // Keep semaphore allocated: late external callbacks must fail by the
        // disposed flag, not by racing against SemaphoreSlim.Dispose().
    }
}
