using System.Xml.Linq;
using Xunit;

namespace SerialBridge.App.Tests;

public sealed class BlacklistInputPresentationTests
{
    [Fact]
    public void BlacklistInputsHavePlaceholdersAndTooltipsWithoutChangingNames()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ClientsWindow.xaml");
        var document = XDocument.Load(file);
        var inputs = document.Descendants()
            .Where(x => x.Name.LocalName == "TextBox")
            .ToDictionary(x => (string?)x.Attribute(XName.Get("Name",
                "http://schemas.microsoft.com/winfx/2006/xaml")) ?? "", x => x);

        Assert.True(inputs.TryGetValue("BlockIpInput", out var ip));
        Assert.True(inputs.TryGetValue("BlockRemarkInput", out var remark));
        Assert.Contains("IP 地址", (string?)ip!.Attribute("PlaceholderText"));
        Assert.Contains("192.168.1.100", (string?)ip.Attribute("PlaceholderText"));
        Assert.Contains("IPv4", (string?)ip.Attribute("ToolTip"));
        Assert.Contains("备注（选填）", (string?)remark!.Attribute("PlaceholderText"));
        Assert.Contains("封禁原因", (string?)remark.Attribute("ToolTip"));

        Assert.Contains(document.Descendants(), x =>
            x.Name.LocalName == "TextBlock" &&
            (string?)x.Attribute("Text") == "全局生效（所有映射）");
        Assert.Contains(document.Descendants(), x =>
            x.Name.LocalName == "Button" &&
            (string?)x.Attribute("Click") == "OnAddBlockedIp");
    }
}
