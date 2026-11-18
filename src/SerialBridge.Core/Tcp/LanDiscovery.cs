using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using SerialBridge.Core.Models;

namespace SerialBridge.Core.Tcp;

/// <summary>UDP discovery: local broadcast plus optional routed unicast targets; no remote control.</summary>
public static class LanDiscoveryProtocol
{
    public const int Port = 39001;
    public const string Magic = "serialbridge-discovery-v1";
    public const int MaxPacketBytes = 8192;
    public const int MaxMappings = 64;
}

public sealed record DiscoveredMapping(Guid Id, string Name, string ComPort, int TcpPort,
    string SerialParameters, string ListenIp = "0.0.0.0")
{
    /// <summary>Use the actual TCP binding for a specific adapter, never
    /// incorrectly substitute the UDP response's other adapter address.</summary>
    public string ConnectAddress(string responseAddress) =>
        string.IsNullOrWhiteSpace(ListenIp) || ListenIp == "0.0.0.0"
            ? responseAddress : ListenIp;
}
public sealed record DiscoveredDevice(Guid DeviceId, string Name, string Address,
    IReadOnlyList<DiscoveredMapping> Mappings);

internal sealed class DiscoveryPacket
{
    public string Magic { get; set; } = "";
    public string Type { get; set; } = "";
    public string RequestId { get; set; } = "";
    public Guid DeviceId { get; set; }
    public string Name { get; set; } = "";
    public List<DiscoveredMapping> Mappings { get; set; } = new();
}

/// <summary>Explicit opt-in UDP responder. Publishes only running TCP mappings.</summary>
public sealed class LanDiscoveryResponder : IAsyncDisposable
{
    private readonly Func<IReadOnlyList<MappingConfig>> _runningMappings;
    private readonly Guid _deviceId;
    private readonly int _port;
    private UdpClient? _socket;
    private CancellationTokenSource? _cancel;
    private Task? _loop;

    public LanDiscoveryResponder(Func<IReadOnlyList<MappingConfig>> runningMappings,
        Guid deviceId, int port = LanDiscoveryProtocol.Port)
    {
        _runningMappings = runningMappings;
        _deviceId = deviceId;
        _port = port;
    }

    public bool IsRunning => _socket is not null;

    public void Start()
    {
        if (_socket is not null) return;
        var udp = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
            _cancel = new CancellationTokenSource();
            _socket = udp;
            _loop = ListenAsync(udp, _cancel.Token);
        }
        catch
        {
            udp.Dispose();
            _cancel?.Dispose();
            _cancel = null;
            throw;
        }
    }

    private async Task ListenAsync(UdpClient udp, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult incoming;
            try { incoming = await udp.ReceiveAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (token.IsCancellationRequested) { break; }
            catch (SocketException) { continue; }

            if (incoming.Buffer.Length > LanDiscoveryProtocol.MaxPacketBytes) continue;
            try
            {
                var query = JsonSerializer.Deserialize<DiscoveryPacket>(incoming.Buffer);
                if (query?.Magic != LanDiscoveryProtocol.Magic ||
                    query.Type != "discover" || !Guid.TryParse(query.RequestId, out _)) continue;

                var mappings = _runningMappings()
                    .Where(m => IPAddress.TryParse(m.ListenIp, out var ip) &&
                        ip.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(ip))
                    .Take(LanDiscoveryProtocol.MaxMappings)
                    .Where(m => m.ListenPort is > 0 and <= 65535)
                    .Select(m => new DiscoveredMapping(m.Id, m.Name, m.PortName,
                        m.ListenPort, m.SerialSummary, m.ListenIp)).ToList();
                var response = new DiscoveryPacket
                {
                    Magic = LanDiscoveryProtocol.Magic,
                    Type = "response",
                    RequestId = query.RequestId,
                    DeviceId = _deviceId,
                    Name = Environment.MachineName,
                    Mappings = mappings,
                };
                // Respect the on-wire byte limit, not only the number of mappings.
                // A long mapping name must not make the entire device invisible.
                var bytes = JsonSerializer.SerializeToUtf8Bytes(response);
                while (bytes.Length > LanDiscoveryProtocol.MaxPacketBytes && response.Mappings.Count > 0)
                {
                    response.Mappings.RemoveAt(response.Mappings.Count - 1);
                    bytes = JsonSerializer.SerializeToUtf8Bytes(response);
                }
                if (bytes.Length <= LanDiscoveryProtocol.MaxPacketBytes)
                    await udp.SendAsync(bytes, incoming.RemoteEndPoint, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (token.IsCancellationRequested) { break; }
            catch (SocketException) when (token.IsCancellationRequested) { break; }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                // Malformed/untrusted discovery packets must not stop service.
            }
        }
    }

    public async Task StopAsync()
    {
        var udp = _socket;
        if (udp is null) return;
        var cancel = _cancel;
        var loop = _loop;
        _socket = null;
        _cancel = null;
        _loop = null;
        try
        {
            cancel?.Cancel();
            udp.Dispose();
            if (loop is not null)
            {
                try { await loop.ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancel?.IsCancellationRequested == true) { }
                catch (ObjectDisposedException) when (cancel?.IsCancellationRequested == true) { }
                catch (SocketException) when (cancel?.IsCancellationRequested == true) { }
            }
        }
        finally
        {
            udp.Dispose();
            cancel?.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}

public static class LanDiscoveryScanner
{
    /// <summary>
    /// Enumerate host addresses in an explicit CIDR. Limit to /24.. /32
    /// (at most 254 hosts) to avoid accidental broad network sweeps.
    /// </summary>
    public static IReadOnlyList<IPAddress> ExpandCidr(string cidr)
    {
        var parts = cidr.Trim().Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork ||
            !int.TryParse(parts[1], out var prefix) || prefix is < 24 or > 32)
            throw new ArgumentException("请输入有效 IPv4 网段（例如 192.168.2.0/24，范围 /24～/32）。");

        var bytes = address.GetAddressBytes();
        // Work in unsigned network-byte order; /24.. /32 always fits 256.
        var raw = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) |
                  ((uint)bytes[2] << 8) | bytes[3];
        var mask = prefix == 32 ? uint.MaxValue : uint.MaxValue << (32 - prefix);
        var network = raw & mask;
        var count = 1u << (32 - prefix);
        // /31 point-to-point prefixes have two valid endpoints and no
        // network/broadcast reservation; /32 is one host.
        var first = prefix >= 31 ? 0u : 1u;
        var end = prefix >= 31 ? count : count - 1;
        var result = new List<IPAddress>();
        for (var offset = first; offset < end; offset++)
        {
            var value = network + offset;
            var ip = new IPAddress(new[]
            {
                (byte)(value >> 24), (byte)(value >> 16),
                (byte)(value >> 8), (byte)value
            });
            if (IPAddress.IsLoopback(ip) || ip.GetAddressBytes()[0] is >= 224 or 0)
                throw new ArgumentException("不支持回环、组播或保留网段扫描。");
            result.Add(ip);
        }
        return result;
    }

    /// <summary>
    /// Broadcast on local IPv4 interfaces. Optionally query a known host by
    /// unicast UDP when routers do not forward broadcasts across subnets.
    /// </summary>
    public static async Task<IReadOnlyList<DiscoveredDevice>> ScanAsync(
        TimeSpan timeout, CancellationToken token = default, IPAddress? targetHost = null,
        string? targetSubnet = null)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        udp.EnableBroadcast = true;
        udp.Client.ReceiveBufferSize = 1024 * 1024;
        var requestId = Guid.NewGuid().ToString("D");
        var packet = JsonSerializer.SerializeToUtf8Bytes(new DiscoveryPacket
        {
            Magic = LanDiscoveryProtocol.Magic, Type = "discover", RequestId = requestId,
        });
        var destinations = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                    unicast.IPv4Mask is not { } mask) continue;
                var ip = unicast.Address.GetAddressBytes();
                var m = mask.GetAddressBytes();
                destinations.Add(new IPAddress(Enumerable.Range(0, 4)
                    .Select(i => (byte)(ip[i] | ~m[i])).ToArray()));
            }
        }
        if (targetHost is not null)
        {
            if (targetHost.AddressFamily != AddressFamily.InterNetwork ||
                IPAddress.IsLoopback(targetHost) ||
                targetHost.Equals(IPAddress.Any) || targetHost.Equals(IPAddress.Broadcast))
                throw new ArgumentException("目标 IP 必须是有效的远程 IPv4 单播地址。", nameof(targetHost));
            destinations.Add(targetHost);
        }
        var subnetHosts = string.IsNullOrWhiteSpace(targetSubnet)
            ? Array.Empty<IPAddress>()
            : ExpandCidr(targetSubnet).ToArray();
        foreach (var address in subnetHosts) destinations.Add(address);

        // Rate-limit directed discovery so a /24 does not become a UDP burst.
        // Local broadcast still happens once per active interface.
        var sent = 0;
        foreach (var address in destinations)
        {
            token.ThrowIfCancellationRequested();
            try { await udp.SendAsync(packet, new IPEndPoint(address, LanDiscoveryProtocol.Port), token)
                .ConfigureAwait(false); }
            catch (SocketException) { } // One unavailable network interface is not fatal.
            if (++sent % 16 == 0 && subnetHosts.Length != 0)
                await Task.Delay(20, token).ConfigureAwait(false);
        }
        var found = new Dictionary<Guid, DiscoveredDevice>();
        using var window = CancellationTokenSource.CreateLinkedTokenSource(token);
        window.CancelAfter(timeout);
        while (!window.Token.IsCancellationRequested)
        {
            try
            {
                var response = await udp.ReceiveAsync(window.Token).ConfigureAwait(false);
                if (response.Buffer.Length > LanDiscoveryProtocol.MaxPacketBytes) continue;
                var p = JsonSerializer.Deserialize<DiscoveryPacket>(response.Buffer);
                if (p?.Magic != LanDiscoveryProtocol.Magic || p.Type != "response" ||
                    p.RequestId != requestId || p.DeviceId == Guid.Empty ||
                    p.Mappings is null || p.Mappings.Count > LanDiscoveryProtocol.MaxMappings) continue;
                var mappings = p.Mappings.Where(m => m is not null &&
                    m.TcpPort is > 0 and <= 65535 &&
                    !string.IsNullOrWhiteSpace(m.ComPort) &&
                    IPAddress.TryParse(m.ConnectAddress(response.RemoteEndPoint.Address.ToString()), out var bindAddress) &&
                    bindAddress.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(bindAddress)).ToArray();
                var name = (p.Name ?? "").Trim();
                if (string.IsNullOrWhiteSpace(name)) name = "未命名设备";
                if (name.Length > 80) name = name[..80];
                found[p.DeviceId] = new DiscoveredDevice(p.DeviceId, name,
                    response.RemoteEndPoint.Address.ToString(), mappings);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { break; }
            catch (SocketException) { break; }
            catch (Exception ex) when (ex is JsonException or NullReferenceException or ArgumentException) { }
        }
        token.ThrowIfCancellationRequested();
        return found.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
