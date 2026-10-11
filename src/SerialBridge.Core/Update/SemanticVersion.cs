namespace SerialBridge.Core.Update;

/// <summary>语义化版本比较（容忍 v 前缀与预发布尾缀）。</summary>
public static class SemanticVersion
{
    /// <summary>candidate 版本新于 current 返回 true（如 1.2.2 vs 1.3.0）。</summary>
    public static bool IsNewer(string current, string candidate)
    {
        if (!TryParse(candidate, out var c)) return false;
        if (!TryParse(current, out var cur)) return false;
        if (c.Major != cur.Major) return c.Major > cur.Major;
        if (c.Minor != cur.Minor) return c.Minor > cur.Minor;
        return c.Patch > cur.Patch;
    }

    public static bool TryParse(string s, out (int Major, int Minor, int Patch) v)
    {
        v = (0, 0, 0);
        if (string.IsNullOrWhiteSpace(s)) return false;
        var parts = s.Trim().TrimStart('v', 'V').Split('.');
        if (parts.Length < 2) return false;
        if (!int.TryParse(parts[0], out var major)) return false;
        if (!int.TryParse(parts[1], out var minor)) return false;
        var patch = 0;
        if (parts.Length >= 3)
        {
            var p = parts[2];
            var dash = p.IndexOf('-');
            if (dash >= 0) p = p[..dash];
            if (!int.TryParse(p, out patch)) return false;
        }
        v = (major, minor, patch);
        return true;
    }
}
