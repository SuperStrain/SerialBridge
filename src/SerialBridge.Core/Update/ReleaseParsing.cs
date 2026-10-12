using System.Text.Json;

namespace SerialBridge.Core.Update;

/// <summary>
/// GitHub Release 信息解析。
/// 关键约定：最新版按"版本号最大"选取，而非 GitHub 的发布时间排序——
/// 连续推两个 tag 时 CI 并行构建可能乱序发布，/releases/latest 会返回错误版本。
/// </summary>
public static class ReleaseParsing
{
    /// <summary>
    /// 从 Release 列表（JSON 数组）或单个 Release（JSON 对象）中选出
    /// 版本号最大的已发布（非 draft、非 prerelease）Release。无有效项返回 false。
    /// </summary>
    public static bool TrySelectHighestRelease(JsonElement root, out JsonElement release, out string tag)
    {
        release = default;
        tag = "";

        if (root.ValueKind == JsonValueKind.Array)
        {
            var found = false;
            (int Major, int Minor, int Patch) bestV = default;
            foreach (var r in root.EnumerateArray())
            {
                if (!TryGetPublishedTag(r, out var t, out var v)) continue;
                if (!found || v.CompareTo(bestV) > 0)
                {
                    found = true;
                    bestV = v;
                    release = r;
                    tag = t;
                }
            }
            return found;
        }

        if (root.ValueKind == JsonValueKind.Object && TryGetPublishedTag(root, out tag, out _))
        {
            release = root;
            return true;
        }
        return false;
    }

    private static bool TryGetPublishedTag(JsonElement r, out string tag, out (int Major, int Minor, int Patch) v)
    {
        tag = "";
        v = default;
        if (r.ValueKind != JsonValueKind.Object) return false;
        if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return false;
        if (r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return false;
        if (!r.TryGetProperty("tag_name", out var t)) return false;
        tag = t.GetString() ?? "";
        return SemanticVersion.TryParse(tag, out v);
    }

    /// <summary>
    /// 从 releases/latest 页面的重定向 Location（如
    /// https://github.com/owner/repo/releases/tag/v1.3.4）提取 tag。无 /tag/ 段返回 false。
    /// </summary>
    public static bool TryParseTagFromRedirect(string location, out string tag)
    {
        tag = "";
        if (string.IsNullOrWhiteSpace(location)) return false;
        var idx = location.IndexOf("/tag/", StringComparison.Ordinal);
        if (idx < 0) return false;
        var s = location[(idx + "/tag/".Length)..];
        var end = s.IndexOfAny(new[] { '?', '#' });
        if (end >= 0) s = s[..end];
        try { s = Uri.UnescapeDataString(s); } catch { /* 保持原样 */ }
        s = s.Trim();
        if (s.Length == 0) return false;
        tag = s;
        return true;
    }
}
