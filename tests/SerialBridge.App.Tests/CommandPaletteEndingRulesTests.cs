using SerialBridge.App.Views;
using Xunit;

namespace SerialBridge.App.Tests;

/// <summary>
/// 命令编辑窗口的换行自动纠正规则：仅在实际切换格式时生效，
/// 用户显式配置的换行（如 Text + None）不得在未切换格式时被覆盖。
/// </summary>
public sealed class CommandPaletteEndingRulesTests
{
    [Fact]
    public void FormatUnchanged_KeepsExplicitEnding()
    {
        // 审查发现的行为回归：重新提交 Text 不得把显式 None 改成 CR
        Assert.Equal("None", CommandPaletteWindow.ComputeEndingAfterFormatChange("Text", "Text", "None"));
        Assert.Equal("CR", CommandPaletteWindow.ComputeEndingAfterFormatChange("Text", "Text", "CR"));
        Assert.Equal("LF", CommandPaletteWindow.ComputeEndingAfterFormatChange("Hex", "Hex", "LF"));
    }

    [Fact]
    public void FormatUnchanged_IgnoreCase_KeepsEnding()
    {
        Assert.Equal("None", CommandPaletteWindow.ComputeEndingAfterFormatChange("text", "Text", "None"));
        Assert.Equal("CRLF", CommandPaletteWindow.ComputeEndingAfterFormatChange("HEX ", "Hex", "CRLF"));
    }

    [Fact]
    public void SwitchToHex_ForcesNone()
    {
        Assert.Equal("None", CommandPaletteWindow.ComputeEndingAfterFormatChange("Text", "Hex", "CR"));
        Assert.Equal("None", CommandPaletteWindow.ComputeEndingAfterFormatChange("Text", "hex", "CRLF"));
    }

    [Fact]
    public void SwitchFromHexToText_RestoresCr()
    {
        Assert.Equal("CR", CommandPaletteWindow.ComputeEndingAfterFormatChange("Hex", "Text", "None"));
    }

    [Fact]
    public void UnknownOriginalFormat_TreatedAsChanged()
    {
        Assert.Equal("CR", CommandPaletteWindow.ComputeEndingAfterFormatChange(null, "Text", "None"));
        Assert.Equal("None", CommandPaletteWindow.ComputeEndingAfterFormatChange(null, "Hex", "CR"));
        Assert.Equal("CR", CommandPaletteWindow.ComputeEndingAfterFormatChange("bogus", "Text", "None"));
    }
}
