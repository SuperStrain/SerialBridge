using System.Xml.Linq;
using Xunit;

namespace SerialBridge.App.Tests;

/// <summary>Protect the approved sidebar/table layout and the truthful help entry.</summary>
public sealed class MainWindowLayoutTests
{
    private static readonly XNamespace Xaml =
        XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");

    private static XDocument Markup() =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MainWindow.xaml"));

    [Fact]
    public void PrimaryNavigation_ContainsOnlyRealDestinations_AndNoLinuxAction()
    {
        var document = Markup();
        var buttons = document.Descendants()
            .Where(e => e.Name.LocalName == "Button").ToArray();
        Assert.Single(buttons.Where(e => (string?)e.Attribute("Click") == "OnOpenRemoteConnections"));
        Assert.Single(buttons.Where(e => (string?)e.Attribute("Click") == "OnOpenSettings"));
        Assert.Single(buttons.Where(e => (string?)e.Attribute("Click") == "OnOpenAbout"));
        Assert.DoesNotContain(buttons, e => (string?)e.Attribute("Click") == "OnOpenLinuxAccess");
        Assert.DoesNotContain(document.Descendants(), e =>
            (string?)e.Attribute("Text") == "Linux 访问");
        Assert.Contains(buttons, e => (string?)e.Attribute("Click") == "OnOpenHelp");
    }

    [Fact]
    public void MappingTable_KeepsLiveCommandsAndAWorkingMoreMenu()
    {
        var document = Markup();
        var bindings = document.Descendants().Attributes("Command")
            .Select(a => a.Value).ToArray();
        foreach (var command in new[]
        {
            "AddMappingCommand", "StartAllCommand", "StopAllCommand",
            "OpenTerminalCommand", "ToggleRunCommand",
            "OpenMonitorCommand", "EditCommand", "DeleteCommand",
            "OpenClientsCommand", "OpenCaptureFolderCommand"
        })
            Assert.Contains(bindings, x => x.Contains(command));
        Assert.Contains(document.Descendants(), e =>
            (string?)e.Attribute("Click") == "OnMappingMoreClick");
        Assert.Contains(document.Descendants(), e => e.Name.LocalName == "ItemsControl" &&
            ((string?)e.Attribute("ItemsSource"))?.Contains("Mappings") == true);
    }

    [Fact]
    public void MappingHeaderAndRows_ShareScrollViewport_WhenManyMappingsOverflow()
    {
        var document = Markup();
        var mappingItems = document.Descendants().Single(e =>
            e.Name.LocalName == "ItemsControl" &&
            ((string?)e.Attribute("ItemsSource"))?.Contains("Mappings") == true);

        var scrollViewers = mappingItems.Ancestors().Where(e =>
            e.Name.LocalName == "ScrollViewer").ToArray();

        // The header and rows must be in the SAME scrolling viewport so
        // a vertical scrollbar cannot shrink only the mapping rows.
        var tableScroll = Assert.Single(scrollViewers);
        Assert.Equal("Auto", (string?)tableScroll.Attribute("VerticalScrollBarVisibility"));
        Assert.Equal("Auto", (string?)tableScroll.Attribute("HorizontalScrollBarVisibility"));

        var tableGrid = tableScroll.Elements().Single(e => e.Name.LocalName == "Grid");
        var header = tableGrid.Elements().Single(e =>
            e.Name.LocalName == "Border" && (string?)e.Attribute("Grid.Row") == "0");
        Assert.Contains(header.Descendants(), e => (string?)e.Attribute("Text") == "映射名称");
        Assert.Equal("1", (string?)mappingItems.Parent?.Attribute("Grid.Row"));
    }

    [Fact]
    public void UpdateBadgeAndFirewallProtectionRemainAccessible()
    {
        var document = Markup();
        Assert.Single(document.Descendants().Where(e =>
            (string?)e.Attribute(Xaml + "Name") == "UpdateBadge"));
        Assert.Contains(document.Descendants().Attributes("Command"),
            a => a.Value.Contains("AddFirewallRuleCommand"));
    }
}
