using SerialBridge.Core.Models;

namespace SerialBridge.App.Services;

/// <summary>
/// 快捷命令栏按钮与确认弹窗共用的预设描述文本。纯字符串逻辑，独立于 UI，便于单元测试。
/// </summary>
public static class CommandPresetText
{
    private const int MaxPreviewLength = 48;
    private const int MaxMacroStepPreviewLength = 16;

    /// <summary>按钮标题（预设名，去除首尾空白）。</summary>
    public static string ButtonTitle(CommandPreset preset) =>
        string.IsNullOrWhiteSpace(preset.Name) ? "未命名命令" : preset.Name.Trim();

    /// <summary>按钮 Tooltip：单步展示内容与换行方式，宏展示步骤摘要。</summary>
    public static string Describe(CommandPreset preset)
    {
        var steps = preset.Steps;
        if (steps is null || steps.Count == 0) return "（无步骤，请编辑补充内容）";
        if (steps.Count == 1)
        {
            var step = steps[0];
            var content = Preview(step.Content, MaxPreviewLength);
            return IsHex(step)
                ? $"HEX：{content}"
                : $"文本：{content}（换行 {EndingLabel(step.Ending)}）";
        }
        var summary = string.Join(" → ", steps.Select(s => Preview(s.Content, MaxMacroStepPreviewLength)));
        return $"宏，共 {steps.Count} 步：{summary}";
    }

    private static bool IsHex(CommandStep step) =>
        string.Equals(step.Format?.Trim(), "Hex", StringComparison.OrdinalIgnoreCase);

    private static string EndingLabel(string? ending) => ending?.Trim().ToUpperInvariant() switch
    {
        "NONE" or "" => "无",
        "CR" => "CR（回车）",
        "LF" => "LF",
        "CRLF" => "CRLF",
        _ => ending!,
    };

    private static string Preview(string? content, int maxLength)
    {
        var text = (content ?? "")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t");
        if (text.Length == 0) return "（空）";
        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }
}
