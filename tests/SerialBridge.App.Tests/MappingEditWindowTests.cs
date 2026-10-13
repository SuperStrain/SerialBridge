using System.Xml.Linq;
using Xunit;

namespace SerialBridge.App.Tests;

public sealed class MappingEditWindowTests
{
    [Fact]
    public void BroadcastModeRadioButton_UsesOneWayBinding()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MappingEditWindow.xaml");
        var document = XDocument.Load(path);
        var presentation = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        var radioButton = document
            .Descendants(presentation + "RadioButton")
            .Single(element => ((string?)element.Attribute("Content"))?.Contains("广播") == true);
        var binding = ((string?)radioButton.Attribute("IsChecked"))?.Replace(" ", "");

        Assert.NotNull(binding);
        Assert.Contains("Mode=OneWay", binding);
    }
}
