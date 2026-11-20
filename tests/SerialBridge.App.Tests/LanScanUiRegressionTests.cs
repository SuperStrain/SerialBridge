using Xunit;

namespace SerialBridge.App.Tests;

/// <summary>
/// Protect scan UI behavior against async double-click regressions.
/// These checks verify the wiring; end-to-end WPF interaction still requires GUI testing.
/// </summary>
public sealed class LanScanUiRegressionTests
{
    private static string Source() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "RemoteConnectionsView.cs"));

    [Fact]
    public void ScanButtonIsTrackedAndConcurrentScanReturnsBeforeStartingAnother()
    {
        var source = Source();
        Assert.Contains("_scanButton = Action(\"扫描局域网\"", source);
        var method = source[source.IndexOf("private async Task ScanAsync()", StringComparison.Ordinal)..];
        var guardIndex = method.IndexOf("if (_isScanning) return;", StringComparison.Ordinal);
        var scanIndex = method.IndexOf("await LanDiscoveryScanner.ScanDetailedAsync(", StringComparison.Ordinal);
        Assert.True(guardIndex >= 0, "Missing scan reentrancy guard.");
        Assert.True(scanIndex >= 0, "Missing detailed discovery scan call.");
        Assert.True(guardIndex < scanIndex, "Reentrancy guard must run before the scan.");
        Assert.Contains("_scanButton.IsEnabled = false;", method);
        Assert.Contains("_discoveryMode.IsEnabled = false;", method);
    }

    [Fact]
    public void ScanResultsAreReplacedAfterAwaitAndButtonsRestoredInFinally()
    {
        var method = Source().Split("private async Task ScanAsync()")[1];
        var scanIndex = method.IndexOf("await LanDiscoveryScanner.ScanDetailedAsync(", StringComparison.Ordinal);
        var clearIndex = method.IndexOf("_discoveredList.Children.Clear();", StringComparison.Ordinal);
        Assert.True(scanIndex >= 0, "Missing detailed discovery scan call.");
        Assert.True(clearIndex >= 0, "Missing result-list clear.");
        Assert.True(scanIndex < clearIndex, "Clear results only after the scan finishes.");
        Assert.Contains("finally", method);
        Assert.Contains("_isScanning = false;", method);
        Assert.Contains("_scanButton.IsEnabled = true;", method);
        Assert.Contains("_scanButton.Content = \"扫描局域网\";", method);
    }
}
