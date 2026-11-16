namespace SerialBridge.Core.Management;

/// <summary>
/// Executes shutdown stages independently. Persisting configuration must happen
/// before sessions are torn down, but an I/O error must never skip cleanup.
/// </summary>
public static class ShutdownCleanup
{
    public static async Task RunAsync(
        Action saveSettings,
        Func<Task> stopDiscovery,
        Func<Task> stopRemotes,
        Func<Task> stopMappings,
        Action<Exception> reportError)
    {
        try { saveSettings(); }
        catch (Exception ex) { reportError(ex); }

        foreach (var stop in new[] { stopDiscovery, stopRemotes, stopMappings })
        {
            try { await stop().ConfigureAwait(false); }
            catch (Exception ex) { reportError(ex); }
        }
    }
}
