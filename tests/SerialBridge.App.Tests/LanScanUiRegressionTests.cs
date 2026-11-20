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
        Assert.True(method.IndexOf("if (_isScanning) return;", StringComparison.Ordinal) <
                    method.IndexOf("await LanDiscoveryScanner.ScanAsync(", StringComparison.Ordinal));
        Assert.Contains("_scanButton.IsEnabled = false;", method);
        Assert.Contains("_discoveryMode.IsEnabled = false;", method);
    }

    [Fact]
    public void ScanResultsAreReplacedAfterAwaitAndButtonsRestoredInFinally()
    {
        var method = Source().Split("private async Task ScanAsync()")[1];
        Assert.True(method.IndexOf("await LanDiscoveryScanner.ScanAsync(", StringComparison.Ordinal) <
                    method.IndexOf("_discoveredList.Children.Clear();", StringComparison.Ordinal));
        Assert.Contains("finally", method);
        Assert.Contains("_isScanning = false;", method);
        Assert.Contains("_scanButton.IsEnabled = true;", method);
        Assert.Contains("_scanButton.Content = \"扫描局域网\";", method);
    }
}
