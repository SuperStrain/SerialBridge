using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SerialBridge.Core.Net;

/// <summary>本机网络信息（用于 UI 展示客户端应连接的局域网地址）。</summary>
public static class NetworkInfo
{
    /// <summary>枚举本机所有活动网卡的 IPv4 地址（不含环回）。</summary>
    public static List<IPAddress> GetLocalIPv4Addresses()
    {
        var result = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !ip.Address.ToString().StartsWith("169.254."))
                    {
                        result.Add(ip.Address);
                    }
                }
            }
        }
        catch { /* 网络信息获取失败时返回已有结果 */ }
        return result.Distinct().ToList();
    }

    /// <summary>获取首选展示地址：取第一个活动 IPv4。</summary>
    public static string GetPrimaryIPv4()
    {
        var ips = GetLocalIPv4Addresses();
        return ips.Count > 0 ? ips[0].ToString() : "127.0.0.1";
    }

    /// <summary>生成形如 "192.168.1.100:9001, 192.168.2.100:9001" 的可访问地址串。</summary>
    public static string DescribeAccessAddresses(string listenIp, int port)
    {
        if (listenIp is not ("0.0.0.0" or "::")) return $"{listenIp}:{port}";
        var ips = GetLocalIPv4Addresses();
        return ips.Count == 0
            ? $"(本机):{port}"
            : string.Join(", ", ips.Select(ip => $"{ip}:{port}"));
    }
}
