namespace SerialBridge.Core.Tcp;

/// <summary>
/// Serializes discovery start/stop and settings persistence as one operation.
/// A later toggle cannot overtake an earlier StopAsync that is still awaiting
/// the UDP receive loop's completion.
/// </summary>
public sealed class LanDiscoverySwitch
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action _start;
    private readonly Func<Task> _stop;
    private readonly Action<bool> _saveEnabled;
    private bool _shuttingDown;

    public LanDiscoverySwitch(Action start, Func<Task> stop, Action<bool> saveEnabled)
    {
        _start = start;
        _stop = stop;
        _saveEnabled = saveEnabled;
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_shuttingDown) return;
            if (enabled) _start();
            else await _stop().ConfigureAwait(false);

            // Only persist after the requested listener transition completes.
            _saveEnabled(enabled);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Stop without changing the saved startup preference.</summary>
    public async Task StopForShutdownAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _shuttingDown = true;
            await _stop().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
