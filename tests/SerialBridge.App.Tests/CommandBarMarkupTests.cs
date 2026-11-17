using System.Xml.Linq;
using Xunit;

namespace SerialBridge.App.Tests;

/// <summary>
/// 命令栏 XAML 结构回归：工具栏按钮切换显隐、栏内停止按钮默认隐藏、
/// 底部提示条保持在最后一行（Grid 行号回归会破坏布局）。
/// 注意 ui:Button 与普通控件命名空间不同，按元素属性跨命名空间匹配。
/// </summary>
public sealed class CommandBarMarkupTests
{
    private static readonly XNamespace Presentation =
        XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml/presentation");
    private static readonly XNamespace Xaml =
        XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");

    private static XDocument LoadTerminalWindowMarkup()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "TerminalWindow.xaml");
        return XDocument.Load(path);
    }

    private static XElement NamedElement(XDocument document, string name) =>
        document.Descendants().Single(element => (string?)element.Attribute(Xaml + "Name") == name);

    [Fact]
    public void CommandBarHost_SitsBetweenTerminalAndFooter()
    {
        var document = LoadTerminalWindowMarkup();
        var host = NamedElement(document, "CommandBarHost");

        // 未加前缀的附加属性（Grid.Row）属于无命名空间属性
        Assert.Equal("3", (string?)host.Attribute("Grid.Row"));
        // 显隐由设置驱动（代码赋值），XAML 不写死 Visible
        Assert.NotEqual("Visible", (string?)host.Attribute("Visibility"));
    }

    [Fact]
    public void ToolbarButton_TogglesBarAndBarButtonsAreWired()
    {
        var document = LoadTerminalWindowMarkup();

        var toggle = NamedElement(document, "ToggleCommandBarButton");
        Assert.Equal("OnCommandsClick", (string?)toggle.Attribute("Click"));

        var stop = NamedElement(document, "CommandBarStop");
        Assert.Equal("Collapsed", (string?)stop.Attribute("Visibility"));
        Assert.Equal("OnCommandBarStopClick", (string?)stop.Attribute("Click"));

        var edit = document.Descendants().Single(element =>
            (string?)element.Attribute("Click") == "OnCommandBarEditClick");
        Assert.Equal("编辑", (string?)edit.Attribute("Content"));
    }

    [Fact]
    public void FooterHint_IsRemoved()
    {
        var document = LoadTerminalWindowMarkup();
        // 底部快捷键帮助小字已按需求整体移除：只剩 4 行（标题栏/工具条/终端/命令栏）
        Assert.Equal(4, document.Descendants(Presentation + "RowDefinition").Count());
        Assert.DoesNotContain(document.Descendants(), element =>
            ((string?)element.Attribute("Text"))?.Contains("逐键透传模式") == true);
    }
}
