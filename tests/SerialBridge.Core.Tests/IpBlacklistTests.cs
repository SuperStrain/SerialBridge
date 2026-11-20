using System.Net;
using System.Net.Sockets;
using SerialBridge.Core.History;
using SerialBridge.Core.Management;
using SerialBridge.Core.Models;
using SerialBridge.Core.Tcp;
using Xunit;

namespace SerialBridge.Core.Tests;

public sealed class IpBlacklistTests
{
    [Theory]
    [InlineData("172.20.8.48", true)]
    [InlineData(" 192.168.1.9 ", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("192.168.1.0/24", false)]
    [InlineData("1.2.3.4:5000", false)]
    [InlineData("bad", false)]
    [InlineData("224.0.0.1", false)]
    public void NormalizationOnlyAcceptsSingleIpv4(string input, bool expected)
    {
        Assert.Equal(expected, IpBlacklistService.TryNormalize(input, out _));
    }

    [Fact]
    public void AddRemoveAndRemarkPersistSnapshots()
    {
        var service = new IpBlacklistService();
        IReadOnlyList<BlockedIpEntry> stored = [];
        void Persist(IReadOnlyList<BlockedIpEntry> items) => stored = items;
        Assert.True(service.Add("172.20.8.48", "设备A", "手动添加", Persist, _ => 0));
        Assert.False(service.Add("172.20.8.48", "", "手动添加", Persist, _ => 0));
        Assert.Single(stored);
        Assert.True(service.IsBlocked(IPAddress.Parse("172.20.8.48")));
        Assert.True(service.UpdateRemark("172.20.8.48", "设备B", Persist));
        Assert.Equal("设备B", service.Entries.Single().Remark);
        Assert.True(service.Remove("172.20.8.48", Persist));
        Assert.Empty(stored);
        Assert.False(service.IsBlocked(IPAddress.Parse("172.20.8.48")));
    }

    [Fact]
    public void PersistenceFailureRollsBackInMemoryAndNeverKicks()
    {
        var service = new IpBlacklistService();
        var kicked = false;
        Assert.Throws<IOException>(() => service.Add("172.20.8.48", "", "",
            _ => throw new IOException("disk"), _ => { kicked = true; return 1; }));
        Assert.False(kicked);
        Assert.Empty(service.Entries);
        Assert.True(service.Add("172.20.8.48", "", "", _ => { }, _ => 0));
        Assert.Throws<IOException>(() => service.Remove("172.20.8.48",
            _ => throw new IOException("disk")));
        Assert.True(service.IsBlocked(IPAddress.Parse("172.20.8.48")));
    }

    [Fact]
    public void AddLocksOutSubsequentAdmissions()
    {
        var service = new IpBlacklistService();
        var ip = IPAddress.Parse("192.0.2.5");
        Assert.True(service.TryAdmit(ip, () => { }));
        Assert.True(service.Add(ip.ToString(), "", "", _ => { }, _ => 0));
        Assert.False(service.TryAdmit(ip, () => throw new Exception("Must not admit")));
        Assert.True(service.TryAdmit(IPAddress.Parse("192.0.2.6"), () => { }));
    }

    [Fact]
    public async Task BlacklistedLoopbackIsRejectedBeforeClientLimitWithHistory()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var dbPath = Path.Combine(Path.GetTempPath(), "SerialBridge.Tests",
            Guid.NewGuid().ToString("N"), "history.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        await using var history = new ClientHistoryStore(dbPath);
        var blacklist = new IpBlacklistService();
        Assert.True(blacklist.Add("127.0.0.1", "test", "手动添加", _ => { }, _ => 0));
        var config = new MappingConfig
        {
            ListenIp = "127.0.0.1", ListenPort = port,
            ClientMode = ClientMode.Single
        };
        await using var server = new TcpServerService(config, history, blacklist);
        server.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        ClientHistoryEntry? rejection = null;
        while (DateTime.UtcNow < deadline)
        {
            rejection = (await history.GetHistoryAsync(config.Id)).FirstOrDefault(
                x => x.DisconnectReason == "IP 黑名单拒绝连接");
            if (rejection is not null) break;
            await Task.Delay(25);
        }
        Assert.NotNull(rejection);
        Assert.Equal(0, server.ClientCount);
        Assert.Equal(0, server.TotalAccepted);
        Assert.Equal(0, rejection.SentBytes);
        Assert.Equal(0, rejection.ReceivedBytes);
        Assert.NotNull(rejection.DisconnectedAtUtc);
        await server.StopAsync();
    }

    [Fact]
    public async Task BlockingOnlineClientRecordsBlacklistedDisconnect()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var path = Path.Combine(Path.GetTempPath(), "SerialBridge.Tests",
            Guid.NewGuid().ToString("N"), "history.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var history = new ClientHistoryStore(path);
        var blacklist = new IpBlacklistService();
        var config = new MappingConfig { ListenIp = "127.0.0.1", ListenPort = port };
        await using var server = new TcpServerService(config, history, blacklist);
        server.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        Assert.True(SpinWait.SpinUntil(() => server.ClientCount == 1, TimeSpan.FromSeconds(5)));
        var kicked = 0;
        Assert.True(blacklist.Add("127.0.0.1", "", "手动添加", _ => { },
            ip => kicked = server.DisconnectIp(ip)));
        Assert.Equal(1, kicked);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        ClientHistoryEntry? ended = null;
        while (DateTime.UtcNow < deadline)
        {
            ended = (await history.GetHistoryAsync(config.Id)).FirstOrDefault(
                x => x.DisconnectReason == "IP 已加入黑名单，服务端主动断开");
            if (ended is not null) break;
            await Task.Delay(25);
        }
        Assert.NotNull(ended);
        Assert.Equal(0, server.ClientCount);
        await server.StopAsync();
    }
}
