using SerialBridge.App.Services;
using SerialBridge.Core.Models;
using Xunit;

namespace SerialBridge.App.Tests;

/// <summary>命令栏按钮标题与 Tooltip 文本的纯逻辑回归。</summary>
public sealed class CommandPresetTextTests
{
    private static CommandPreset Preset(string name, params CommandStep[] steps)
    {
        var preset = new CommandPreset { Name = name };
        preset.Steps.Clear();
        preset.Steps.AddRange(steps);
        return preset;
    }

    [Fact]
    public void ButtonTitle_TrimsNameAndFallsBackWhenEmpty()
    {
        Assert.Equal("ls", CommandPresetText.ButtonTitle(new CommandPreset { Name = " ls " }));
        Assert.Equal("未命名命令", CommandPresetText.ButtonTitle(new CommandPreset { Name = "   " }));
    }

    [Fact]
    public void Describe_SingleTextStep_ShowsContentAndEnding()
    {
        var preset = Preset("列表", new CommandStep { Format = "Text", Content = "uname -a", Ending = "CR" });
        Assert.Equal("文本：uname -a（换行 CR（回车））", CommandPresetText.Describe(preset));
    }

    [Fact]
    public void Describe_SingleHexStep_ShowsHexContentWithoutEnding()
    {
        var preset = Preset("帧", new CommandStep { Format = "Hex", Content = "01 03 FF", Ending = "None" });
        Assert.Equal("HEX：01 03 FF", CommandPresetText.Describe(preset));
    }

    [Fact]
    public void Describe_Macro_SummarizesStepsInOrder()
    {
        var preset = Preset("宏",
            new CommandStep { Format = "Text", Content = "ls", Ending = "CR" },
            new CommandStep { Format = "Text", Content = "uname -a", Ending = "CR" });
        Assert.Equal("宏，共 2 步：ls → uname -a", CommandPresetText.Describe(preset));
    }

    [Fact]
    public void Describe_EscapesInvisibleCharactersInPreview()
    {
        var preset = Preset("多行", new CommandStep { Format = "Text", Content = "a\rb\nc", Ending = "CR" });
        Assert.Equal("文本：a\\rb\\nc（换行 CR（回车））", CommandPresetText.Describe(preset));
    }

    [Fact]
    public void Describe_EmptyContentAndSteps_AreMarkedExplicitly()
    {
        var noSteps = new CommandPreset();
        noSteps.Steps.Clear();
        Assert.Equal("（无步骤，请编辑补充内容）", CommandPresetText.Describe(noSteps));
        var empty = Preset("空", new CommandStep { Format = "Text", Content = "", Ending = "CR" });
        Assert.Equal("文本：（空）（换行 CR（回车））", CommandPresetText.Describe(empty));
    }

    [Fact]
    public void ConfirmMessage_ListsTargetAndStepCount()
    {
        var target = new MappingConfig { Name = "调试板", PortName = "COM3" };
        var preset = Preset("reboot", new CommandStep { Content = "reboot" });
        Assert.Equal("向 调试板 (COM3) 发送「reboot」？\n共 1 步。",
            CommandPresetText.ConfirmMessage(target, preset));
    }
}
