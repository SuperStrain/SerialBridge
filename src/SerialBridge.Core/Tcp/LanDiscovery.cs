using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using SerialBridge.Core.Models;

namespace SerialBridge.Core.Tcp;

/// <summary>Local-subnet discovery only; no remote configuration or control.</summary>
public static class LanDiscoveryProtocol
{
    public const int Port = 39001;
    public const string Magic = "serialbridge-discovery-v1";
    public const int MaxPacketBytes = 8192;
    public const int MaxMappings = 64;
}

public sealed record DiscoveredMapping(Guid Id, string Name, string ComPort, int TcpPort,
    string SerialParameters);
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
    private UdpClient? _socket;
    private CancellationTokenSource? _cancel;
    private Task? _loop;

    public LanDiscoveryResponder(Func<IReadOnlyList<MappingConfig>> runningMappings,
        Guid deviceId)
    {
        _runningMappings = runningMappings;
        _deviceId = deviceId;
    }

    public bool IsRunning => _socket is not null;

    public void Start()
    {
        if (_socket is not null) return;
        var udp = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, LanDiscoveryProtocol.Port));
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

                var mappings = _runningMappings().Take(LanDiscoveryProtocol.MaxMappings)
                    .Where(m => m.ListenPort is > 0 and <= 65535)
                    .Select(m => new DiscoveredMapping(m.Id, m.Name, m.PortName,
                        m.ListenPort, m.SerialSummary)).ToList();
                var response = new DiscoveryPacket
                {
                    Magic = LanDiscoveryProtocol.Magic,
                    Type = "response",
                    RequestId = query.RequestId,
                    DeviceId = _deviceId,
                    Name = Environment.MachineName,
                    Mappings = mappings,
                };
                var bytes = JsonSerializer.SerializeToUtf8Bytes(response);
                if (bytes.Length <= LanDiscoveryProtocol.MaxPacketBytes)
                    await udp.SendAsync(bytes, incoming.RemoteEndPoint, token).ConfigureAwait(false);
            }
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
        _socket = null;
        var cancel = _cancel;
        _cancel = null;
        cancel?.Cancel();
        udp.Dispose();
        if (_loop is { } task)
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        _loop = null;
        cancel?.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}

public static class LanDiscoveryScanner
{
    /// <summary>Send broadcast per IPv4 interface; no port scans and no implicit TCP connections.</summary>
    public static async Task<IReadOnlyList<DiscoveredDevice>> ScanAsync(
        TimeSpan timeout, CancellationToken token = default)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        udp.EnableBroadcast = true;
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
        foreach (var address in destinations)
        {
            try { await udp.SendAsync(packet, new IPEndPoint(address, LanDiscoveryProtocol.Port), token)
                .ConfigureAwait(false); }
            catch (SocketException) { } // One unavailable network interface is not fatal.
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
                var mappings = p.Mappings.Where(m => m.TcpPort is > 0 and <= 65535 &&
                    !string.IsNullOrWhiteSpace(m.ComPort)).ToArray();
                found[p.DeviceId] = new DiscoveredDevice(p.DeviceId,
                    p.Name.Length > 80 ? p.Name[..80] : p.Name,
                    response.RemoteEndPoint.Address.ToString(), mappings);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { break; }
            catch (SocketException) { break; }
            catch (JsonException) { }
        }
        token.ThrowIfCancellationRequested();
        return found.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
