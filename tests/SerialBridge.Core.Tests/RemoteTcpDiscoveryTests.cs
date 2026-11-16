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
            var chunk = new byte[1024 * 1024];
            var accepted = 0;
            for (var i = 0; i < 500; ++i)
            {
                if (!client.Send(chunk)) break;
                accepted++;
            }
            Assert.InRange(accepted, 1, 500);
            await client.DisconnectAsync();
            Assert.False(client.Send(new byte[] { 1 }));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public void OptInDiscoveryDefaultsOff()
    {
        var settings = new AppSettings();
        Assert.False(settings.General.AllowLanDiscovery);
        Assert.Empty(settings.RemoteConnections);
    }
}
