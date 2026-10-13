using System.Xml.Linq;
using SerialBridge.App.Views;
using Xunit;

namespace SerialBridge.App.Tests;

public sealed class TerminalWindowPresentationTests
{
    [Fact]
    public void InitialMarkup_HidesUnpaintedWindowAndWebViewBehindDarkPlaceholder()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "TerminalWindow.xaml");
        var document = XDocument.Load(path);
        var presentation = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        var xaml = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");

        Assert.Equal("0", (string?)document.Root?.Attribute("Opacity"));

        var webView = document.Descendants().Single(element =>
            (string?)element.Attribute(xaml + "Name") == "Web");
        Assert.Equal("Hidden", (string?)webView.Attribute("Visibility"));
        Assert.Equal("#1E1E2E", (string?)webView.Attribute("DefaultBackgroundColor"));

        var loadingHost = document.Descendants(presentation + "Border").Single(element =>
            (string?)element.Attribute(xaml + "Name") == "TerminalLoadingHost");
        Assert.Equal("#1E1E2E", (string?)loadingHost.Attribute("Background"));
    }

    [Fact]
    public void PresentationTransitions_KeepDarkSurfaceUntilBrowserIsReadyAndHideBrowserFirstOnClose()
    {
        var calls = new List<string>();

        TerminalWindowPresentation.RevealBrowser(
            () => calls.Add("show-browser"),
            () => calls.Add("hide-placeholder"));
        TerminalWindowPresentation.HideForClose(
            () => calls.Add("hide-browser"),
            () => calls.Add("hide-window"));

        Assert.Equal(
            ["show-browser", "hide-placeholder", "hide-browser", "hide-window"],
            calls);
    }
}
