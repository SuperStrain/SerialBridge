using System.Management;
using System.Text.RegularExpressions;

namespace SerialBridge.App.Services;

/// <summary>串口下拉选项：端口名 + 友好设备描述（如 "COM7 · USB-SERIAL CH340"）。</summary>
public sealed record PortOption(string Port, string Description)
{
    public string Display => Description.Length > 0 ? $"{Port} · {Description}" : Port;
}

/// <summary>
/// 带设备描述的串口枚举（WMI Win32_PnPEntity）。
/// 目的：区分蓝牙虚拟串口与 USB 物理串口，避免用户误选（COM3 蓝牙口 vs COM7 CH340）。
/// </summary>
public static class PortEnumeration
{
    private static readonly Regex ComNameRegex = new(@"\( *(COM\d{1,3}) *\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static List<PortOption> GetPortOptions()
    {
        var byPort = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%COM%'");
            foreach (var o in searcher.Get())
            {
                var name = o["Name"] as string;
                if (string.IsNullOrEmpty(name)) continue;
                var m = ComNameRegex.Match(name);
                if (!m.Success) continue;
                var port = m.Groups[1].Value.ToUpperInvariant();
                var desc = name[..m.Index].Trim().TrimEnd('(').Trim();
                byPort[port] = desc;
            }
        }
        catch
        {
            // WMI 查询失败时退化为无描述列表
        }

        // 兜底：WMI 没列出的口（罕见驱动）也加入，无描述
        foreach (var p in Core.Config.SerialPortInfo.GetPortNames())
        {
            var upper = p.ToUpperInvariant();
            if (!byPort.ContainsKey(upper)) byPort[upper] = "";
        }

        return byPort
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new PortOption(kv.Key, kv.Value))
            .ToList();
    }
}
