using SerialBridge.Core.Models;
using SerialBridge.Core.Tcp;

namespace SerialBridge.Core.Management;

/// <summary>
/// Independent outgoing TCP targets. Network waits use per-target gates: one
/// unreachable host must never block other targets, deletion or app shutdown.
/// </summary>
public sealed class RemoteConnectionManager : IAsyncDisposable
{
    private sealed class Intent
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public Task? RetryTask { get; set; }
        // Guarded by _sync, never derived from Task.IsCompleted.
        public bool RetryRunning { get; set; }
        public bool LostDuringRetry { get; set; }
    }

    private sealed class Target
    {
        public required RemoteTcpSession Session { get; init; }
        public required Action<string> LostHandler { get; init; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Intent? Current { get; set; }
    }

    private readonly List<Target> _targets = new();
    private readonly object _sync = new();
    private bool _disposed;
    public event Action? Changed;

    public IReadOnlyList<RemoteTcpSession> Sessions
    {
        get { lock (_sync) return _targets.Select(x => x.Session).ToArray(); }
    }

    public RemoteTcpSession Add(RemoteConnectionConfig config)
    {
        Target target;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Validate(config.Host, config.Port);
            if (config.Id == Guid.Empty || _targets.Any(x => x.Session.Config.Id == config.Id))
                config.Id = Guid.NewGuid();
            var session = new RemoteTcpSession(config);
            Action<string> handler = _ => OnConnectionLost(session);
            target = new Target { Session = session, LostHandler = handler };
            session.ConnectionLost += handler;
            _targets.Add(target);
        }
        Changed?.Invoke();
        return target.Session;
    }

    private static void Validate(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535)
            throw new ArgumentException("远程地址或 TCP 端口无效");
    }

    private Target RequireTarget(RemoteTcpSession session)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _targets.FirstOrDefault(t => ReferenceEquals(t.Session, session))
                ?? throw new InvalidOperationException("连接已删除");
        }
    }

    private static void CancelIntent(Intent? intent)
    {
        intent?.Cancellation.Cancel();
        // The token may still be in use by an in-flight ConnectAsync. A
        // cancellation source is disposed only after that target's gate releases.
    }

    private static void DisposeIntentLater(Intent? intent)
    {
        if (intent is null) return;
        if (intent.RetryTask is { } task)
            _ = task.ContinueWith(_ => intent.Cancellation.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        else intent.Cancellation.Dispose();
    }

    /// <summary>Switch intent synchronously, so queued operations can cancel an in-flight connect.</summary>
    private (Target target, Intent? previous, Intent current) BeginConnect(RemoteTcpSession session)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var target = _targets.FirstOrDefault(t => ReferenceEquals(t.Session, session))
                ?? throw new InvalidOperationException("连接已删除");
            var previous = target.Current;
            CancelIntent(previous);
            var current = new Intent();
            target.Current = current;
            return (target, previous, current);
        }
    }

    public async Task ConnectAsync(RemoteTcpSession session)
    {
        var (target, previous, current) = BeginConnect(session);
        await target.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Old connection operations are finished once this target gate is held.
            DisposeIntentLater(previous);
            current.Cancellation.Token.ThrowIfCancellationRequested();
            await session.ConnectAsync(current.Cancellation.Token).ConfigureAwait(false);
        }
        catch
        {
            lock (_sync)
            {
                if (ReferenceEquals(target.Current, current) &&
                    !current.Cancellation.IsCancellationRequested)
                {
                    if (session.Config.AutoReconnect)
                        ScheduleRetryLocked(target, current);
                    else
                    {
                        target.Current = null;
                        // Current token is not disposed until ConnectAsync unwinds.
                    }
                }
            }
            throw;
        }
        finally
        {
            target.Gate.Release();
            lock (_sync)
                if (target.Current is null) DisposeIntentLater(current);
        }
        Changed?.Invoke();
    }

    private void OnConnectionLost(RemoteTcpSession session)
    {
        lock (_sync)
        {
            if (_disposed) return;
            var target = _targets.FirstOrDefault(t => ReferenceEquals(t.Session, session));
            if (target?.Current is not { } current || !session.Config.AutoReconnect ||
                current.Cancellation.IsCancellationRequested) return;
            if (current.RetryRunning)
            {
                current.LostDuringRetry = true;
                return;
            }
            ScheduleRetryLocked(target, current);
        }
    }

    /// <summary>Caller holds _sync; RetryRunning is cleared atomically before completion.</summary>
    private void ScheduleRetryLocked(Target target, Intent intent)
    {
        if (_disposed || intent.Cancellation.IsCancellationRequested || intent.RetryRunning ||
            !ReferenceEquals(target.Current, intent)) return;
        intent.RetryRunning = true;
        intent.LostDuringRetry = false;
        intent.RetryTask = RetryLoopAsync(target, intent);
    }

    private async Task RetryLoopAsync(Target target, Intent intent)
    {
        var token = intent.Cancellation.Token;
        var delay = 3000;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                    await target.Gate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        lock (_sync)
                            if (_disposed || !ReferenceEquals(target.Current, intent))
                                return;
                        await target.Session.ConnectAsync(token).ConfigureAwait(false);
                        Changed?.Invoke();
                        return;
                    }
                    finally { target.Gate.Release(); }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception) { delay = Math.Min(30000, delay * 2); }
            }
        }
        finally
        {
            lock (_sync)
            {
                // Never consult Task.IsCompleted: it changes AFTER this finally.
                // Clear the scheduling flag under the same lock used by the event.
                intent.RetryRunning = false;
                if (intent.LostDuringRetry && !target.Session.IsConnected &&
                    !_disposed && ReferenceEquals(target.Current, intent) &&
                    !token.IsCancellationRequested)
                    ScheduleRetryLocked(target, intent);
                intent.LostDuringRetry = false;
            }
        }
    }

    /// <summary>Cancel immediately, before waiting for a potentially stalled network connect.</summary>
    private (Target target, Intent? previous) BeginStop(RemoteTcpSession session, bool remove)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var target = _targets.FirstOrDefault(t => ReferenceEquals(t.Session, session))
                ?? throw new InvalidOperationException("连接已删除");
            var previous = target.Current;
            target.Current = null;
            CancelIntent(previous);
            if (remove)
            {
                _targets.Remove(target);
                session.ConnectionLost -= target.LostHandler;
            }
            return (target, previous);
        }
    }

    public async Task DisconnectAsync(RemoteTcpSession session)
    {
        var (target, previous) = BeginStop(session, remove: false);
        await target.Gate.WaitAsync().ConfigureAwait(false);
        try { await session.DisconnectAsync().ConfigureAwait(false); }
        finally
        {
            target.Gate.Release();
            DisposeIntentLater(previous);
        }
        Changed?.Invoke();
    }

    public async Task RemoveAsync(RemoteTcpSession session)
    {
        var (target, previous) = BeginStop(session, remove: true);
        await target.Gate.WaitAsync().ConfigureAwait(false);
        try { await session.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            target.Gate.Release();
            DisposeIntentLater(previous);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Edits are serialized with that target's connection attempts. Never
    /// redirect an already connecting TCP session by mutating shared Config.
    /// </summary>
    public async Task UpdateConfigurationAsync(RemoteTcpSession session,
        string name, string host, int port, bool autoReconnect, bool autoConnectOnStartup)
    {
        Validate(host, port);
        var (target, previous) = BeginStop(session, remove: false);
        await target.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await session.DisconnectAsync().ConfigureAwait(false);
            session.Config.Name = string.IsNullOrWhiteSpace(name) ? "远程设备" : name.Trim();
            session.Config.Host = host.Trim();
            session.Config.Port = port;
            session.Config.AutoReconnect = autoReconnect;
            session.Config.AutoConnectOnStartup = autoConnectOnStartup;
        }
        finally
        {
            target.Gate.Release();
            DisposeIntentLater(previous);
        }
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        Target[] targets;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            targets = _targets.ToArray();
            foreach (var target in targets) CancelIntent(target.Current);
            foreach (var target in targets) target.Session.ConnectionLost -= target.LostHandler;
        }
        // Targets are independent: don't serialize shutdown on remote timeouts.
        await Task.WhenAll(targets.Select(async target =>
        {
            await target.Gate.WaitAsync().ConfigureAwait(false);
            try { await target.Session.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                target.Gate.Release();
                DisposeIntentLater(target.Current);
            }
        })).ConfigureAwait(false);
        // Keep session configurations available to save during shutdown.
    }
}
