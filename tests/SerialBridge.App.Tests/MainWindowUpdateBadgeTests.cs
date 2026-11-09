using System.Xml.Linq;
using Xunit;

namespace SerialBridge.App.Tests;

/// <summary>
/// 主界面更新入口的 XAML 结构回归：NEW 徽章位于页头标题旁（可点击、绿色配色、
/// 绑定徽章显隐），旧升级横幅已移除，统计卡片不再依赖横幅的互斥可见性。
/// 注意 ui: 与普通控件命名空间不同，按元素属性跨命名空间匹配。
/// </summary>
public sealed class MainWindowUpdateBadgeTests
{
    private static readonly XNamespace Xaml =
        XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");

    private static XDocument LoadMainWindowMarkup()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MainWindow.xaml");
        return XDocument.Load(path);
    }

    private static XElement NamedElement(XDocument document, string name) =>
        document.Descendants().Single(element => (string?)element.Attribute(Xaml + "Name") == name);

    [Fact]
    public void NewBadge_SitsInHeader_WithGreenStylingAndClickHandler()
    {
        var document = LoadMainWindowMarkup();
        var badge = NamedElement(document, "UpdateBadge");

        Assert.Equal("OnUpdateBadgeClick", (string?)badge.Attribute("MouseLeftButtonDown"));
        Assert.Equal("#1A16A34A", (string?)badge.Attribute("Background"));
        Assert.Contains("UpdateBadgeVisible", (string?)badge.Attribute("Visibility"));
        Assert.Contains("UpdateBadgeVersion", (string?)badge.Attribute("ToolTip"));

        var label = badge.Descendants().Single(element => (string?)element.Attribute("Text") == "NEW");
        Assert.Equal("#16A34A", (string?)label.Attribute("Foreground"));
    }

    [Fact]
    public void OldUpdateBanner_IsRemoved()
    {
        var document = LoadMainWindowMarkup();

        // 旧横幅的可见性绑定与按钮内容不得再出现
        Assert.DoesNotContain(document.Descendants(),
            element => ((string?)element.Attribute("Visibility"))?.Contains("UpdateBannerVisible") == true);
        Assert.DoesNotContain(document.Descendants(),
            element => (string?)element.Attribute("Content") == "立即升级");

        // 更新弹窗的绑定源仍在（更新/跳过按钮由弹窗承载，主界面仅保留徽章）
        Assert.Contains(document.Descendants(),
            element => ((string?)element.Attribute(Xaml + "Name")) == "UpdateBadge");
    }

    [Fact]
    public void StatsCards_NoLongerDependOnBannerVisibility()
    {
        var document = LoadMainWindowMarkup();

        // 统计卡（4 列 UniformGrid）不再随横幅显隐切换
        var statsGrid = document.Descendants()
            .Single(element => element.Name.LocalName == "UniformGrid");
        Assert.Equal("4", (string?)statsGrid.Attribute("Columns"));
        Assert.Null((string?)statsGrid.Attribute("Visibility"));
    }
}
