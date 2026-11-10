using SerialBridge.App.Views;
using SerialBridge.Core.Models;
using Xunit;

namespace SerialBridge.App.Tests;

/// <summary>
/// 命令编辑窗口的延时编辑规则：延时是「与下一步的间隔」，
/// 最后一行（含单步命令）没有下一步，其延时单元格应禁用；
/// 已填数值保留，追加步骤后立即恢复可编辑并自动生效。
/// </summary>
public sealed class CommandPaletteDelayRulesTests
{
    [Fact]
    public void SingleStepPreset_TailDelayDisabled()
    {
        var preset = new CommandPreset(); // 默认自带一个步骤
        Assert.False(CommandPaletteWindow.DelayEditable(preset, preset.Steps[0]));
    }

    [Fact]
    public void MultiStepPreset_LastStepDisabled_MiddleStepsEditable()
    {
        var preset = new CommandPreset { Steps =
        [
            new CommandStep { Content = "one" },
            new CommandStep { Content = "two" },
            new CommandStep { Content = "three" }
        ] };
        Assert.True(CommandPaletteWindow.DelayEditable(preset, preset.Steps[0]));
        Assert.True(CommandPaletteWindow.DelayEditable(preset, preset.Steps[1]));
        Assert.False(CommandPaletteWindow.DelayEditable(preset, preset.Steps[^1]));
    }

    [Fact]
    public void TailBecomesEditable_AfterAppendingStep()
    {
        var preset = new CommandPreset { Steps = [new CommandStep { Content = "only" }] };
        Assert.False(CommandPaletteWindow.DelayEditable(preset, preset.Steps[0]));
        preset.Steps.Add(new CommandStep { Content = "two" });
        Assert.True(CommandPaletteWindow.DelayEditable(preset, preset.Steps[0]));
        Assert.False(CommandPaletteWindow.DelayEditable(preset, preset.Steps[^1]));
    }

    [Fact]
    public void NullPresetOrStep_Disabled()
    {
        Assert.False(CommandPaletteWindow.DelayEditable(null, new CommandStep()));
        Assert.False(CommandPaletteWindow.DelayEditable(new CommandPreset(), null));
    }
}
