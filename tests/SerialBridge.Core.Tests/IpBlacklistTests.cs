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
    public void BlacklistSurvivesConfigSaveAndLoad()
    {
        var path = Path.Combine(Path.GetTempPath(), "SerialBridge.Tests",
            Guid.NewGuid().ToString("N"), "config.json");
        var store = new SerialBridge.Core.Config.ConfigStore(path);
        var config = new AppSettings();
        config.IpBlacklist.Add(new BlockedIpEntry
        {
            IpAddress = "172.20.8.48", Remark = "阻止测试客户端",
            AddedAtUtc = DateTime.UtcNow, Source = "手动添加"
        });
        store.Save(config);
        var loaded = new SerialBridge.Core.Config.ConfigStore(path).Load();
        Assert.Single(loaded.IpBlacklist);
        Assert.Equal("172.20.8.48", loaded.IpBlacklist[0].IpAddress);
        Assert.Equal("阻止测试客户端", loaded.IpBlacklist[0].Remark);
        Assert.True(new IpBlacklistService(loaded.IpBlacklist)
            .IsBlocked(IPAddress.Parse("172.20.8.48")));
    }

    [Fact]
    public async Task AdmissionAndBlockingSerializeWithoutLeakingNewConnection()
    {
        var service = new IpBlacklistService();
        var ip = IPAddress.Parse("192.0.2.99");
        using var admitted = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var accept = Task.Run(() => service.TryAdmit(ip, () =>
        {
            admitted.Set();
            release.Wait(TimeSpan.FromSeconds(5));
        }));
        Assert.True(admitted.Wait(TimeSpan.FromSeconds(5)));
        var kickCount = 0;
        var ban = Task.Run(() => service.Add(ip.ToString(), "", "", _ => { }, _ =>
        {
            Interlocked.Increment(ref kickCount);
            return 1;
        }));
        try
        {
            Assert.False(ban.IsCompleted);
        }
        finally { release.Set(); }
        Assert.True(await accept);
        Assert.True(await ban);
        Assert.Equal(1, kickCount);
        Assert.False(service.TryAdmit(ip, () => throw new Exception("leaked")));
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
    public async Task ManualKickOnlyClosesSelectedSessionWithCorrectReason()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var path = Path.Combine(Path.GetTempPath(), "SerialBridge.Tests",
            Guid.NewGuid().ToString("N"), "history.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var history = new ClientHistoryStore(path);
        var config = new MappingConfig
        {
            ListenIp = "127.0.0.1", ListenPort = port,
            ClientMode = ClientMode.Multi, MaxClients = 3
        };
        await using var server = new TcpServerService(config, history);
        server.Start();
        using var first = new TcpClient();
        using var second = new TcpClient();
        await first.ConnectAsync(IPAddress.Loopback, port);
        await second.ConnectAsync(IPAddress.Loopback, port);
        Assert.True(SpinWait.SpinUntil(() => server.ClientCount == 2, TimeSpan.FromSeconds(5)));
        var target = server.Clients.First().Id;
        Assert.True(server.DisconnectClient(target));
        Assert.False(server.DisconnectClient(target));
        Assert.True(SpinWait.SpinUntil(() => server.ClientCount == 1, TimeSpan.FromSeconds(5)));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        ClientHistoryEntry? ended = null;
        while (DateTime.UtcNow < deadline)
        {
            ended = (await history.GetHistoryAsync(config.Id)).FirstOrDefault(
                item => item.DisconnectReason == "服务端手动断开");
            if (ended is not null) break;
            await Task.Delay(25);
        }
        Assert.NotNull(ended);
        Assert.Equal(1, server.ClientCount);
        await server.StopAsync();
    }

    [Fact]
    public async Task OneBanKicksAllMappingsSharingTheSameRemoteIp()
    {
        var probe1 = new TcpListener(IPAddress.Loopback, 0);
        var probe2 = new TcpListener(IPAddress.Loopback, 0);
        probe1.Start();
        probe2.Start();
        var probes = new[]
        {
            ((IPEndPoint)probe1.LocalEndpoint).Port,
            ((IPEndPoint)probe2.LocalEndpoint).Port
        };
        probe1.Stop();
        probe2.Stop();
        var blacklist = new IpBlacklistService();
        await using var first = new TcpServerService(new MappingConfig
        {
            ListenIp = "127.0.0.1", ListenPort = probes[0]
        }, blacklist: blacklist);
        await using var second = new TcpServerService(new MappingConfig
        {
            ListenIp = "127.0.0.1", ListenPort = probes[1]
        }, blacklist: blacklist);
        first.Start();
        second.Start();
        using var c1 = new TcpClient();
        using var c2 = new TcpClient();
        await c1.ConnectAsync(IPAddress.Loopback, probes[0]);
        await c2.ConnectAsync(IPAddress.Loopback, probes[1]);
        Assert.True(SpinWait.SpinUntil(
            () => first.ClientCount == 1 && second.ClientCount == 1,
            TimeSpan.FromSeconds(5)));
        var kicked = 0;
        Assert.True(blacklist.Add("127.0.0.1", "", "测试", _ => { },
            ip => kicked = first.DisconnectIp(ip) + second.DisconnectIp(ip)));
        Assert.Equal(2, kicked);
        Assert.True(SpinWait.SpinUntil(
            () => first.ClientCount == 0 && second.ClientCount == 0,
            TimeSpan.FromSeconds(5)));
        await first.StopAsync();
        await second.StopAsync();
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
