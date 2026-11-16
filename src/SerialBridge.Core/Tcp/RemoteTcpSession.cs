using SerialBridge.Core.Models;
using System.Net.Sockets;
using System.Threading.Channels;

namespace SerialBridge.Core.Tcp;

/// <summary>
/// Outgoing raw TCP session. Never buffers user commands across a disconnect.
/// Closing a terminal view does not stop this session; caller owns its lifetime.
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
    private int _generation;
    private bool _disposed;
    private string _lastDisconnectReason = "未连接";

    public RemoteConnectionConfig Config { get; }
    public bool IsConnected => _connected;
    public string LastDisconnectReason => _lastDisconnectReason;
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
            if (_cancel is not null) return;
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
                    SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite,
                });
                _client = client;
                _cancel = cancel;
                _output = output;
                _connected = true;
                _lastDisconnectReason = "";
                var generation = ++_generation;
                _worker = RunAsync(client, output, cancel, generation);
                StatusChanged?.Invoke();
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
        finally { _lifecycle.Release(); }
    }

    public bool Send(byte[] data)
    {
        if (data.Length == 0) return IsConnected;
        // Clone: caller may reuse its memory after the method returns.
        return _connected && _output?.Writer.TryWrite(data.ToArray()) == true;
    }

    private async Task RunAsync(TcpClient client, Channel<byte[]> output,
        CancellationTokenSource cancellation, int generation)
    {
        var token = cancellation.Token;
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
                    if (n == 0) break;
                    Interlocked.Add(ref _received, n);
                    DataReceived?.Invoke(buffer.AsSpan(0, n).ToArray());
                }
            }
            catch (IOException ex) { reason = "远程连接异常：" + ex.Message; }
            catch (SocketException ex) { reason = "网络错误：" + ex.SocketErrorCode; }
            finally
            {
                cancellation.Cancel();
                output.Writer.TryComplete();
                client.Dispose();
                try { await writer.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            if (generation == Volatile.Read(ref _generation))
            {
                _connected = false;
                _lastDisconnectReason = reason;
                StatusChanged?.Invoke();
                ConnectionLost?.Invoke(reason);
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

    public async Task DisconnectAsync()
    {
        Task? worker;
        CancellationTokenSource? cancellation;
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cancel is null) return;
            _connected = false;
            ++_generation; // suppress late stale close notifications while deliberately stopping
            cancellation = _cancel;
            cancellation.Cancel();
            _output?.Writer.TryComplete();
            _client?.Dispose();
            worker = _worker;
            _worker = null;
            _client = null;
            _output = null;
            _cancel = null;
            StatusChanged?.Invoke();
        }
        finally { _lifecycle.Release(); }
        if (worker is not null)
            try { await worker.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        cancellation?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _disposed = true;
        _lifecycle.Dispose();
    }
}
