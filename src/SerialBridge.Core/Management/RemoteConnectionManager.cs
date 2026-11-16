using SerialBridge.Core.Models;
using SerialBridge.Core.Tcp;

namespace SerialBridge.Core.Management;

/// <summary>
/// Owns saved remote endpoints, one retry loop per endpoint and all connection
/// lifetimes. Never retransmits bytes across disconnect/reconnect.
/// </summary>
public sealed class RemoteConnectionManager : IAsyncDisposable
{
    private sealed class RetryState
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public Task? Task { get; set; }
        public bool LostDuringRetry { get; set; }
    }

    private readonly List<RemoteTcpSession> _sessions = new();
    private readonly Dictionary<Guid, RetryState> _states = new();
    private readonly Dictionary<Guid, Action<string>> _handlers = new();
    private readonly object _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public IReadOnlyList<RemoteTcpSession> Sessions
    {
        get { lock (_sync) return _sessions.ToArray(); }
    }

    public event Action? Changed;

    public RemoteTcpSession Add(RemoteConnectionConfig config)
    {
        RemoteTcpSession session;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (config.Id == Guid.Empty || _sessions.Any(s => s.Config.Id == config.Id))
                config.Id = Guid.NewGuid();
            if (string.IsNullOrWhiteSpace(config.Host) || config.Port is < 1 or > 65535)
                throw new ArgumentException("远程地址或端口无效");
            session = new RemoteTcpSession(config);
            // Exactly one stable subscription per session, not one per ConnectAsync.
            Action<string> handler = _ => OnConnectionLost(session);
            session.ConnectionLost += handler;
            _sessions.Add(session);
            _handlers.Add(config.Id, handler);
        }
        Changed?.Invoke();
        return session;
    }

    private void OnConnectionLost(RemoteTcpSession session)
    {
        lock (_sync)
        {
            if (_disposed || !session.Config.AutoReconnect ||
                !_states.TryGetValue(session.Config.Id, out var state) ||
                state.Cancellation.IsCancellationRequested) return;
            if (state.Task is { IsCompleted: false })
            {
                // If the just-reconnected peer disappears before this retry
                // task returns, start another retry as the task completes.
                state.LostDuringRetry = true;
                return;
            }
            state.LostDuringRetry = false;
            state.Task = RetryLoopAsync(session, state);
        }
    }

    private async Task RetryLoopAsync(RemoteTcpSession session, RetryState state)
    {
        var token = state.Cancellation.Token;
        var delay = 3000;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                    await _gate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        lock (_sync)
                        {
                            if (_disposed || !_states.TryGetValue(session.Config.Id, out var current) ||
                                !ReferenceEquals(current, state) || !_sessions.Contains(session))
                                return;
                        }
                        await session.ConnectAsync(token).ConfigureAwait(false);
                        Changed?.Invoke();
                        return;
                    }
                    finally { _gate.Release(); }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception) { delay = Math.Min(30000, delay * 2); }
            }
        }
        finally
        {
            lock (_sync)
            {
                if (!_disposed && !token.IsCancellationRequested &&
                    _states.TryGetValue(session.Config.Id, out var current) &&
                    ReferenceEquals(current, state) &&
                    state.LostDuringRetry && !session.IsConnected)
                {
                    state.LostDuringRetry = false;
                    state.Task = RetryLoopAsync(session, state);
                }
                else
                {
                    state.LostDuringRetry = false;
                }
            }
        }
    }

    private static void Retire(RetryState? state)
    {
        if (state is null) return;
        state.Cancellation.Cancel();
        if (state.Task is { } task)
            _ = task.ContinueWith(_ => state.Cancellation.Dispose(),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        else
            state.Cancellation.Dispose();
    }

    private RetryState? TakeState(Guid id)
    {
        lock (_sync)
        {
            if (!_states.Remove(id, out var state)) return null;
            // Cancel immediately, including a pending retry acquiring _gate.
            state.Cancellation.Cancel();
            return state;
        }
    }

    public async Task ConnectAsync(RemoteTcpSession session)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_sessions.Contains(session)) throw new InvalidOperationException("连接已删除");
            }
            var prior = TakeState(session.Config.Id);
            Retire(prior);
            var state = new RetryState();
            lock (_sync) _states[session.Config.Id] = state;
            try { await session.ConnectAsync(state.Cancellation.Token).ConfigureAwait(false); }
            catch
            {
                lock (_sync)
                    if (_states.TryGetValue(session.Config.Id, out var current) &&
                        ReferenceEquals(current, state)) _states.Remove(session.Config.Id);
                Retire(state);
                throw;
            }
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }

    public async Task DisconnectAsync(RemoteTcpSession session)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Retire(TakeState(session.Config.Id));
            await session.DisconnectAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }

    public async Task RemoveAsync(RemoteTcpSession session)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Retire(TakeState(session.Config.Id));
            lock (_sync)
            {
                _sessions.Remove(session);
                if (_handlers.Remove(session.Config.Id, out var handler))
                    session.ConnectionLost -= handler;
            }
            await session.DisposeAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (var state in _states.Values) Retire(state);
                _states.Clear();
                foreach (var session in _sessions)
                    if (_handlers.Remove(session.Config.Id, out var handler))
                        session.ConnectionLost -= handler;
            }
            foreach (var session in Sessions)
                await session.DisposeAsync().ConfigureAwait(false);
            // Keep saved config list available through shutdown/save, even after
            // physical sockets are disposed.
        }
        finally { _gate.Release(); }
    }
}
