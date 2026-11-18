using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using SerialBridge.Core.Models;
using SerialBridge.Core.Tcp;
using Xunit;

namespace SerialBridge.Core.Tests;

public sealed class RemoteTcpDiscoveryTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public void SubnetDiscovery_ExpandsOnlyHostAddresses()
    {
        var hosts = LanDiscoveryScanner.ExpandCidr("192.168.2.42/24");
        Assert.Equal(254, hosts.Count);
        Assert.Equal("192.168.2.1", hosts[0].ToString());
        Assert.Equal("192.168.2.254", hosts[^1].ToString());
        Assert.Equal(new[] { "10.0.0.8" },
            LanDiscoveryScanner.ExpandCidr("10.0.0.8/32")
                .Select(address => address.ToString()));
        Assert.Equal(2, LanDiscoveryScanner.ExpandCidr("10.0.0.4/30").Count);
    }

    [Theory]
    [InlineData("192.168.2.0/23")]
    [InlineData("192.168.2.0/33")]
    [InlineData("192.168.2.0")]
    [InlineData("not-an-ip/24")]
    [InlineData("127.0.0.1/32")]
    [InlineData("224.0.0.1/32")]
    public void SubnetDiscovery_RejectsInvalidOrUnsafeRanges(string cidr)
    {
        Assert.Throws<ArgumentException>(() => LanDiscoveryScanner.ExpandCidr(cidr));
    }

    [Fact]
    public async Task RemoteSession_ExchangesRawBytesAndSurvivesTerminalOwnership()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var remote = new RemoteTcpSession(new RemoteConnectionConfig
        { Name = "demo", Host = "127.0.0.1", Port = port });
        await remote.ConnectAsync();
        using var server = await listener.AcceptTcpClientAsync();
        var data = new byte[] { 0, 1, 0xFF, 13, 10 };
        Assert.True(remote.Send(data));
        var got = new byte[data.Length];
        await server.GetStream().ReadExactlyAsync(got).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(data, got);
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.DataReceived += bytes => received.TrySetResult(bytes);
        await server.GetStream().WriteAsync(data);
        Assert.Equal(data, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(data.Length, remote.SentBytes);
        Assert.Equal(data.Length, remote.ReceivedBytes);
        await remote.DisconnectAsync();
        Assert.False(remote.IsConnected);
        Assert.False(remote.Send(new byte[] { 1 }));
        listener.Stop();
    }

    [Fact]
    public async Task RemoteSession_QuickPeerCloseShowsTentativeLimitHint()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var remote = new RemoteTcpSession(new RemoteConnectionConfig
        {
            Host = "127.0.0.1",
            Port = ((IPEndPoint)listener.LocalEndpoint).Port
        });
        try
        {
            var lost = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            remote.ConnectionLost += reason => lost.TrySetResult(reason);
            await remote.ConnectAsync();
            using var accepted = await listener.AcceptTcpClientAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            accepted.Client.Shutdown(SocketShutdown.Both);
            // The event is raised only AFTER the disconnect reason is published.
            var reason = await lost.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(remote.IsConnected);
            Assert.True(remote.WasQuickDisconnect);
            Assert.Contains("可能已达服务端连接数上限", reason);
            Assert.Equal(reason, remote.LastDisconnectReason);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task RemoteSession_PeerSendingBytesBeforeClosingIsNotClassifiedAsLimit()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var remote = new RemoteTcpSession(new RemoteConnectionConfig
        {
            Host = "127.0.0.1",
            Port = ((IPEndPoint)listener.LocalEndpoint).Port
        });
        try
        {
            var lost = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            remote.ConnectionLost += reason => lost.TrySetResult(reason);
            await remote.ConnectAsync();
            using var accepted = await listener.AcceptTcpClientAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            await accepted.GetStream().WriteAsync(new byte[] { 1 });
            Assert.True(SpinWait.SpinUntil(() => remote.ReceivedBytes >= 1,
                TimeSpan.FromSeconds(5)));
            accepted.Client.Shutdown(SocketShutdown.Both);
            await lost.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(remote.IsConnected);
            Assert.False(remote.WasQuickDisconnect);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task RemoteSession_ManualDisconnectDoesNotClaimServerLimit()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var remote = new RemoteTcpSession(new RemoteConnectionConfig
        {
            Host = "127.0.0.1",
            Port = ((IPEndPoint)listener.LocalEndpoint).Port
        });
        try
        {
            await remote.ConnectAsync();
            using var accepted = await listener.AcceptTcpClientAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            await remote.DisconnectAsync();
            Assert.False(remote.IsConnected);
            Assert.False(remote.WasQuickDisconnect);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task DiscoveryResponder_IsSilentUntilEnabled_AndOnlyPublishesActiveMappings()
    {
        using var portProbe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)portProbe.Client.LocalEndPoint!).Port;
        portProbe.Dispose();
        var mappings = new[]
        {
            new MappingConfig { Name = "COM3 调试", PortName = "COM3",
                ListenIp = "0.0.0.0", ListenPort = 9001 },
            new MappingConfig { Name = "本机专用", PortName = "COM7",
                ListenIp = "127.0.0.1", ListenPort = 9002 },
        };
        await using var responder = new LanDiscoveryResponder(() => mappings, Guid.NewGuid(), port);
        Assert.False(responder.IsRunning);
        responder.Start();
        Assert.True(responder.IsRunning);
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var id = Guid.NewGuid().ToString("D");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Magic = LanDiscoveryProtocol.Magic, Type = "discover", RequestId = id
        });
        await udp.SendAsync(bytes, new IPEndPoint(IPAddress.Loopback, port));
        var result = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using var json = JsonDocument.Parse(result.Buffer);
        Assert.Equal(id, json.RootElement.GetProperty("RequestId").GetString());
        Assert.Equal("response", json.RootElement.GetProperty("Type").GetString());
        var found = json.RootElement.GetProperty("Mappings");
        Assert.Equal(1, found.GetArrayLength());
        Assert.Equal("COM3", found[0].GetProperty("ComPort").GetString());
        await responder.StopAsync();
        Assert.False(responder.IsRunning);
    }

    [Fact]
    public async Task DiscoveryResponder_StopDuringReplyTraffic_AllowsRestartAndCleanStop()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        probe.Dispose();
        var mappings = Enumerable.Range(0, LanDiscoveryProtocol.MaxMappings)
            .Select(i => new MappingConfig
            {
                Name = "Long mapping " + new string('X', 120),
                PortName = $"COM{i + 1}",
                ListenIp = "0.0.0.0",
                ListenPort = 9001 + i
            }).ToArray();
        await using var responder = new LanDiscoveryResponder(() => mappings, Guid.NewGuid(), port);
        using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var request = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Magic = LanDiscoveryProtocol.Magic,
            Type = "discover",
            RequestId = Guid.NewGuid().ToString("D")
        });

        // Keep requests arriving while the responder is canceled, including
        // the window where it is serializing or sending a response.
        for (var round = 0; round < 10; round++)
        {
            responder.Start();
            Assert.True(responder.IsRunning);
            using var trafficCancel = new CancellationTokenSource();
            var traffic = Task.Run(async () =>
            {
                while (!trafficCancel.IsCancellationRequested)
                {
                    try
                    {
                        await sender.SendAsync(request,
                            new IPEndPoint(IPAddress.Loopback, port), trafficCancel.Token);
                    }
                    catch (OperationCanceledException) when (trafficCancel.IsCancellationRequested)
                    {
                        break;
                    }
                    await Task.Yield();
                }
            });
            try
            {
                await Task.Delay(20);
                await responder.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(responder.IsRunning);
            }
            finally
            {
                trafficCancel.Cancel();
                await traffic.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        responder.Start();
        Assert.True(responder.IsRunning);
        await responder.StopAsync();
        Assert.False(responder.IsRunning);
    }

    [Fact]
    public async Task DiscoveryResponder_TruncatesOversizedMappingListInsteadOfDroppingDevice()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        probe.Dispose();
        var mappings = Enumerable.Range(0, LanDiscoveryProtocol.MaxMappings)
            .Select(i => new MappingConfig
            {
                Name = $"Mapping-{i}-" + new string('X', 200),
                PortName = $"COM{i + 1}",
                ListenIp = "0.0.0.0",
                ListenPort = 10000 + i
            }).ToArray();
        await using var responder = new LanDiscoveryResponder(() => mappings, Guid.NewGuid(), port);
        responder.Start();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var id = Guid.NewGuid().ToString("D");
        var request = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Magic = LanDiscoveryProtocol.Magic, Type = "discover", RequestId = id
        });
        await udp.SendAsync(request, new IPEndPoint(IPAddress.Loopback, port));
        var response = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.InRange(response.Buffer.Length, 1, LanDiscoveryProtocol.MaxPacketBytes);
        using var json = JsonDocument.Parse(response.Buffer);
        Assert.Equal(id, json.RootElement.GetProperty("RequestId").GetString());
        var returned = json.RootElement.GetProperty("Mappings");
        Assert.InRange(returned.GetArrayLength(), 1, LanDiscoveryProtocol.MaxMappings - 1);
        Assert.Equal("COM1", returned[0].GetProperty("ComPort").GetString());
    }

    [Fact]
    public async Task RemoteSession_FailedConnectionDoesNotBlockRetry()
    {
        var port = FreePort();
        await using var remote = new RemoteTcpSession(new RemoteConnectionConfig
        { Host = "127.0.0.1", Port = port });
        await Assert.ThrowsAnyAsync<Exception>(() => remote.ConnectAsync());
        Assert.False(remote.IsConnected);
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            await remote.ConnectAsync();
            Assert.True(remote.IsConnected);
            using var server = await listener.AcceptTcpClientAsync();
            await remote.DisconnectAsync();
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task RemoteSession_ReconnectsAfterRemoteClosesWithoutExplicitDisconnect()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            await using var client = new RemoteTcpSession(new RemoteConnectionConfig
            { Host = "127.0.0.1", Port = port });
            var lost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.ConnectionLost += reason => lost.TrySetResult(reason);
            await client.ConnectAsync();
            using (var server = await listener.AcceptTcpClientAsync())
                server.Client.Shutdown(SocketShutdown.Both);
            await lost.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(client.IsConnected);
            await client.ConnectAsync(); // Previously returned without opening a socket.
            Assert.True(client.IsConnected);
            using var second = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(client.Send(new byte[] { 0x7F }));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task RemoteManager_CanDisconnectAndReconnectWithoutDuplicateLostEvents()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            await using var manager = new SerialBridge.Core.Management.RemoteConnectionManager();
            var session = manager.Add(new RemoteConnectionConfig
            { Host = "127.0.0.1", Port = port, AutoReconnect = false });
            await manager.ConnectAsync(session);
            using (var first = await listener.AcceptTcpClientAsync())
                await manager.DisconnectAsync(session);
            await manager.ConnectAsync(session);
            using (var second = await listener.AcceptTcpClientAsync())
                await manager.DisconnectAsync(session);
            Assert.Single(manager.Sessions);
            Assert.False(session.IsConnected);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task RemoteManager_AutoReconnectsAcrossSuccessivePeerDisconnects()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            await using var manager = new SerialBridge.Core.Management.RemoteConnectionManager();
            var session = manager.Add(new RemoteConnectionConfig
            { Host = "127.0.0.1", Port = port, AutoReconnect = true });
            await manager.ConnectAsync(session);
            using (var first = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5)))
                first.Client.Shutdown(SocketShutdown.Both);
            using (var second = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(12)))
                second.Client.Shutdown(SocketShutdown.Both);
            using var third = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(12));
            // Accept may finish before ConnectAsync updates the client state.
            Assert.True(SpinWait.SpinUntil(() => session.IsConnected, TimeSpan.FromSeconds(3)));
            await manager.DisconnectAsync(session);
            Assert.False(session.IsConnected);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task OutgoingQueue_NeverUsesSilentDropWriteMode()
    {
        // On a backpressured peer, Send() may return false. It must never
        // return true for an element that was discarded by the channel.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            await using var client = new RemoteTcpSession(new RemoteConnectionConfig
            { Host = "127.0.0.1", Port = port });
            await client.ConnectAsync();
            using var server = await listener.AcceptTcpClientAsync();
            // The peer does not read; large messages eventually stall the writer.
            var chunk = new byte[8192];
            var accepted = 0;
            for (var i = 0; i < 5000; ++i)
            {
                if (!client.Send(chunk)) break;
                accepted++;
            }
            Assert.InRange(accepted, 1, 5000);
            await client.DisconnectAsync();
            Assert.False(client.Send(new byte[] { 1 }));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task RemoteConnections_RemainPersistableAfterManagerShutdown()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"sb-remote-{Guid.NewGuid():N}.json");
        try
        {
            await using var manager = new SerialBridge.Core.Management.RemoteConnectionManager();
            manager.Add(new RemoteConnectionConfig
            {
                Name = "远程 COM3", Host = "192.168.1.100", Port = 9001,
                AutoReconnect = true, AutoConnectOnStartup = true
            });
            await manager.DisposeAsync();
            var store = new SerialBridge.Core.Config.ConfigStore(configPath);
            store.Save(new AppSettings { RemoteConnections = manager.Sessions.Select(s => s.Config).ToList() });
            var restored = store.Load();
            var target = Assert.Single(restored.RemoteConnections);
            Assert.Equal("远程 COM3", target.Name);
            Assert.Equal("192.168.1.100", target.Host);
            Assert.Equal(9001, target.Port);
            Assert.True(target.AutoReconnect);
            Assert.True(target.AutoConnectOnStartup);
            Assert.False(restored.General.AllowLanDiscovery);
        }
        finally { File.Delete(configPath); }
    }

    [Fact]
    public void Discovery_UsesSpecificNicAddressInsteadOfUnrelatedUdpSource()
    {
        var boundToSecondAdapter = new DiscoveredMapping(
            Guid.NewGuid(), "设备串口", "COM3", 9001, "115200 8N1", "10.10.0.5");
        var boundToAllAdapters = new DiscoveredMapping(
            Guid.NewGuid(), "设备串口", "COM4", 9002, "115200 8N1");
        Assert.Equal("10.10.0.5", boundToSecondAdapter.ConnectAddress("192.168.1.100"));
        Assert.Equal("192.168.1.100", boundToAllAdapters.ConnectAddress("192.168.1.100"));
    }

    [Fact]
    public async Task RemoteManager_UnreachableHostDoesNotBlockIndependentLocalTarget()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            await using var manager = new SerialBridge.Core.Management.RemoteConnectionManager();
            var slow = manager.Add(new RemoteConnectionConfig
            { Name = "不可达设备", Host = "192.0.2.1", Port = 65534 });
            var fast = manager.Add(new RemoteConnectionConfig
            { Name = "本机模拟设备", Host = "127.0.0.1", Port = port });
            // Do not await the unreachable address first.
            var slowAttempt = manager.ConnectAsync(slow);
            await manager.ConnectAsync(fast).WaitAsync(TimeSpan.FromSeconds(4));
            using var peer = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(4));
            Assert.True(fast.IsConnected);
            await manager.DisconnectAsync(slow);
            try { await slowAttempt; }
            catch (Exception) { /* expected timeout/cancellation */ }
            await manager.DisconnectAsync(fast);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task RemoteManager_EditingConnectionDisconnectsOldHostBeforeChangingTarget()
    {
        var first = new TcpListener(IPAddress.Loopback, 0);
        first.Start();
        var second = new TcpListener(IPAddress.Loopback, 0);
        second.Start();
        try
        {
            var firstPort = ((IPEndPoint)first.LocalEndpoint).Port;
            var secondPort = ((IPEndPoint)second.LocalEndpoint).Port;
            await using var manager = new SerialBridge.Core.Management.RemoteConnectionManager();
            var session = manager.Add(new RemoteConnectionConfig
            { Name = "初始目标", Host = "127.0.0.1", Port = firstPort });
            await manager.ConnectAsync(session);
            using var firstConnection = await first.AcceptTcpClientAsync();
            await manager.UpdateConfigurationAsync(session, "新目标", "127.0.0.1",
                secondPort, autoReconnect: false, autoConnectOnStartup: false);
            Assert.False(session.IsConnected);
            Assert.Equal(secondPort, session.Config.Port);
            Assert.Equal("新目标", session.Config.Name);
            await manager.ConnectAsync(session);
            using var secondConnection = await second.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(session.IsConnected);
            await manager.DisconnectAsync(session);
        }
        finally { first.Stop(); second.Stop(); }
    }

    [Fact]
    public void OptInDiscoveryDefaultsOff()
    {
        var settings = new AppSettings();
        Assert.False(settings.General.AllowLanDiscovery);
        Assert.Empty(settings.RemoteConnections);
    }
}
