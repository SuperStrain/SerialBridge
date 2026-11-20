using System.Net;
using System.Net.Sockets;
using SerialBridge.Core.Models;

namespace SerialBridge.Core.Management;

/// <summary>
/// In-process global IPv4 admission policy. The same lock serializes changes
/// and admission so a newly banned IP cannot slip through during a kick sweep.
/// </summary>
public sealed class IpBlacklistService
{
    public const int MaxEntries = 1000;
    private readonly object _gate = new();
    private readonly Dictionary<string, BlockedIpEntry> _entries = new(StringComparer.Ordinal);

    public IpBlacklistService(IEnumerable<BlockedIpEntry>? initial = null)
    {
        foreach (var item in initial ?? [])
        {
            if (!TryNormalize(item.IpAddress, out var ip) || _entries.ContainsKey(ip)) continue;
            if (_entries.Count == MaxEntries) break;
            _entries.Add(ip, new BlockedIpEntry
            {
                IpAddress = ip, Remark = item.Remark ?? "",
                AddedAtUtc = item.AddedAtUtc, Source = item.Source ?? ""
            });
        }
    }

    public static bool TryNormalize(string? input, out string ip)
    {
        ip = "";
        if (!IPAddress.TryParse(input?.Trim(), out var parsed) ||
            parsed.AddressFamily != AddressFamily.InterNetwork ||
            parsed.Equals(IPAddress.Any) || parsed.Equals(IPAddress.Broadcast) ||
            parsed.GetAddressBytes()[0] >= 224)
            return false;
        // Require four dotted decimal components, not IPAddress.Parse's
        // abbreviated / integer representations.
        var parts = input!.Trim().Split('.');
        if (parts.Length != 4 || parts.Any(p => p.Length is < 1 or > 3 ||
            p.Any(ch => ch is < '0' or > '9'))) return false;
        ip = parsed.ToString();
        return true;
    }

    public IReadOnlyList<BlockedIpEntry> Entries
    {
        get { lock (_gate) return _entries.Values.Select(Clone).OrderBy(x => x.IpAddress).ToArray(); }
    }

    public bool IsBlocked(IPAddress ip)
    {
        lock (_gate) return _entries.ContainsKey(ip.ToString());
    }

    /// <summary>Admission callback runs atomically against policy changes.</summary>
    public bool TryAdmit(IPAddress ip, Action accept)
    {
        lock (_gate)
        {
            if (_entries.ContainsKey(ip.ToString())) return false;
            accept();
            return true;
        }
    }

    /// <summary>
    /// Persist first; on failure roll back in-memory state. Kick while holding
    /// admission lock so no connection is admitted between insert and sweep.
    /// </summary>
    public bool Add(string input, string remark, string source,
        Action<IReadOnlyList<BlockedIpEntry>> persist, Func<IPAddress, int> kick)
    {
        if (!TryNormalize(input, out var normalized))
            throw new ArgumentException("请输入有效的单个 IPv4 地址。", nameof(input));
        lock (_gate)
        {
            if (_entries.ContainsKey(normalized)) return false;
            if (_entries.Count >= MaxEntries) throw new InvalidOperationException("黑名单最多 1000 条。");
            var entry = new BlockedIpEntry
            {
                IpAddress = normalized, Remark = (remark ?? "").Trim(),
                AddedAtUtc = DateTime.UtcNow, Source = source
            };
            _entries.Add(normalized, entry);
            try { persist(_entries.Values.Select(Clone).ToArray()); }
            catch { _entries.Remove(normalized); throw; }
            kick(IPAddress.Parse(normalized));
            return true;
        }
    }

    public bool Remove(string input, Action<IReadOnlyList<BlockedIpEntry>> persist)
    {
        if (!TryNormalize(input, out var normalized))
            throw new ArgumentException("请输入有效的单个 IPv4 地址。", nameof(input));
        lock (_gate)
        {
            if (!_entries.Remove(normalized, out var old)) return false;
            try { persist(_entries.Values.Select(Clone).ToArray()); }
            catch { _entries.Add(normalized, old); throw; }
            return true;
        }
    }

    public bool UpdateRemark(string input, string remark, Action<IReadOnlyList<BlockedIpEntry>> persist)
    {
        if (!TryNormalize(input, out var ip)) throw new ArgumentException("IP 格式无效。", nameof(input));
        lock (_gate)
        {
            if (!_entries.TryGetValue(ip, out var entry)) return false;
            var original = entry.Remark;
            entry.Remark = (remark ?? "").Trim();
            try { persist(_entries.Values.Select(Clone).ToArray()); }
            catch { entry.Remark = original; throw; }
            return true;
        }
    }

    private static BlockedIpEntry Clone(BlockedIpEntry entry) => new()
    {
        IpAddress = entry.IpAddress, Remark = entry.Remark,
        AddedAtUtc = entry.AddedAtUtc, Source = entry.Source
    };
}
