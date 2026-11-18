using Wpf.Ui.Appearance;
using Xunit;

namespace SerialBridge.App.Tests;

public sealed class ThemeSwitchTests
{
    [Theory]
    [InlineData("Light", ApplicationTheme.Light)]
    [InlineData("Dark", ApplicationTheme.Dark)]
    public void ExplicitThemeModesResolveCorrectly(string setting, ApplicationTheme expected)
    {
        Assert.Equal(expected, SerialBridge.App.App.GetEffectiveTheme(setting));
    }

    [Fact]
    public void InvalidThemeFallsBackToLight()
    {
        Assert.Equal(ApplicationTheme.Light, SerialBridge.App.App.GetEffectiveTheme("unexpected"));
    }
}
