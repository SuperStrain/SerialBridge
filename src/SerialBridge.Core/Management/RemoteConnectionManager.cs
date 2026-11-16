using SerialBridge.Core.Models;
using SerialBridge.Core.Tcp;

namespace SerialBridge.Core.Management;

/// <summary>Owns independently configured outgoing TCP sessions and reconnection loops.</summary>
public sealed class RemoteConnectionManager : IAsyncDisposable
{
    private readonly List<RemoteTcpSession> _sessions = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _reconnects = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public IReadOnlyList<RemoteTcpSession> Sessions => _sessions.ToArray();
    public event Action? Changed;

    public RemoteTcpSession Add(RemoteConnectionConfig config)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RemoteConnectionManager));
        if (config.Id == Guid.Empty || _sessions.Any(s => s.Config.Id == config.Id))
            config.Id = Guid.NewGuid();
        if (string.IsNullOrWhiteSpace(config.Host) || config.Port is < 1 or > 65535)
            throw new ArgumentException("远程地址或端口无效");
        var session = new RemoteTcpSession(config);
        _sessions.Add(session);
        Changed?.Invoke();
        return session;
    }

    public async Task ConnectAsync(RemoteTcpSession session)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_sessions.Contains(session)) return;
            CancelRetry(session.Config.Id);
            var cancel = new CancellationTokenSource();
            _reconnects[session.Config.Id] = cancel;
            session.ConnectionLost += _ => RetryIfEnabled(session);
            await session.ConnectAsync(cancel.Token).ConfigureAwait(false);
        }
        catch
        {
            CancelRetry(session.Config.Id);
            throw;
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }

    private void RetryIfEnabled(RemoteTcpSession session)
    {
        if (_disposed || !session.Config.AutoReconnect ||
            !_reconnects.TryGetValue(session.Config.Id, out var cancel) ||
            cancel.IsCancellationRequested) return;
        _ = RetryLoopAsync(session, cancel.Token);
    }

    private async Task RetryLoopAsync(RemoteTcpSession session, CancellationToken token)
    {
        var delay = 3000;
        while (!token.IsCancellationRequested && !_disposed)
        {
            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
                await session.DisconnectAsync().ConfigureAwait(false);
                await session.ConnectAsync(token).ConfigureAwait(false);
                Changed?.Invoke();
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { delay = Math.Min(30000, delay * 2); }
        }
    }

    private void CancelRetry(Guid id)
    {
        if (!_reconnects.Remove(id, out var cancel)) return;
        cancel.Cancel();
        // Cancellation source disposed after all operations have observed its
        // cancellation; async retry tasks themselves own their lifetime.
    }

    public async Task DisconnectAsync(RemoteTcpSession session)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            CancelRetry(session.Config.Id);
            await session.DisconnectAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }

    public async Task RemoveAsync(RemoteTcpSession session)
    {
        await DisconnectAsync(session);
        _sessions.Remove(session);
        await session.DisposeAsync();
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        foreach (var cancel in _reconnects.Values) cancel.Cancel();
        _reconnects.Clear();
        foreach (var session in _sessions.ToArray())
            await session.DisposeAsync();
        _sessions.Clear();
        _gate.Dispose();
    }
}
