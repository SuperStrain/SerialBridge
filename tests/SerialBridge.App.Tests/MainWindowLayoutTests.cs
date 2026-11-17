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
        // “关于”不再作为侧边导航入口，仅保留右上角帮助菜单中的入口。
        Assert.DoesNotContain(buttons, e => (string?)e.Attribute("Click") == "OnOpenAbout");
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
    public void MappingHeaderAndRows_ShareScrollViewport_WithoutHorizontalScroll()
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
        // 横向不允许拖动：列随宽度自适应收缩，操作按钮通过显现等级收纳/展开。
        Assert.Equal("Disabled", (string?)tableScroll.Attribute("HorizontalScrollBarVisibility"));

        var tableGrid = tableScroll.Elements().Single(e => e.Name.LocalName == "Grid");
        var header = tableGrid.Elements().Single(e =>
            e.Name.LocalName == "Border" && (string?)e.Attribute("Grid.Row") == "0");
        Assert.Contains(header.Descendants(), e => (string?)e.Attribute("Text") == "映射名称");
        Assert.Equal("1", (string?)mappingItems.Parent?.Attribute("Grid.Row"));

        // 行操作区包含 5 个渐进显现的快捷图标与"···"收纳菜单
        var rowTemplate = document.Descendants().Single(e =>
            e.Name.LocalName == "DataTemplate" &&
            (string?)e.Attribute(Xaml + "Key") == "MappingCardTemplate");
        foreach (var symbol in new[] { "PeopleTeam24", "ArrowSwap24", "Folder24", "EditSettings24", "DeleteDismiss24" })
            Assert.Contains(rowTemplate.Descendants(), e => (string?)e.Attribute("Symbol") == symbol);
        Assert.Contains(rowTemplate.Descendants(), e =>
            ((string?)e.Attribute("Visibility"))?.Contains("IntBelowToVisibility") == true);
    }

    [Fact]
    public void MappingPortName_BindsToNotifyingViewModelProperty()
    {
        var document = Markup();
        var rowTemplate = document.Descendants().Single(e =>
            e.Name.LocalName == "DataTemplate" &&
            (string?)e.Attribute(Xaml + "Key") == "MappingCardTemplate");
        Assert.Contains(rowTemplate.Descendants(), e =>
            (string?)e.Attribute("Text") == "{Binding PortName, Mode=OneWay}");
        Assert.DoesNotContain(rowTemplate.Descendants(), e =>
            ((string?)e.Attribute("Text"))?.Contains("Config.PortName") == true);
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
