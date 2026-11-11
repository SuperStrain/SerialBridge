using SerialBridge.Core.History;
using SerialBridge.Core.Tcp;
using SerialBridge.Core.Models;
using System.Net;
using System.Net.Sockets;

namespace SerialBridge.Core.Tests;

[CollectionDefinition("ClientHistoryExclusive", DisableParallelization = true)]
public sealed class ClientHistoryCollection { }

[Collection("ClientHistoryExclusive")]
public sealed class ClientHistoryTests
{
    private static string NewDatabasePath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SerialBridgeHistoryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "history.db");
    }

    [Fact]
    public async Task PersistAcrossRestartAndKeepSixFields()
    {
        var file = NewDatabasePath();
        var mapping = Guid.NewGuid();
        var id = Guid.NewGuid();
        var start = DateTime.UtcNow.AddSeconds(-5);
        await using (var store = new ClientHistoryStore(file))
        {
            store.RecordConnected(mapping, id, "192.168.1.2:51000", start);
            store.RecordClosed(mapping, id, start, start.AddSeconds(5), 123, 45, "客户端主动断开");
            await store.FlushAsync();
        }
        await using var reopened = new ClientHistoryStore(file);
        var records = await reopened.GetHistoryAsync(mapping);
        var row = Assert.Single(records);
        Assert.Equal(id, row.Id);
        Assert.Equal("192.168.1.2:51000", row.RemoteAddress);
        Assert.Equal(5000L, row.DurationMs);
        Assert.Equal(123, row.SentBytes);
        Assert.Equal(45, row.ReceivedBytes);
        Assert.Equal("客户端主动断开", row.DisconnectReason);
        Assert.Equal(start.Ticks / TimeSpan.TicksPerMillisecond,
            row.ConnectedAtUtc.Ticks / TimeSpan.TicksPerMillisecond);
        Assert.NotNull(row.DisconnectedAtUtc);
    }

    [Fact]
    public async Task ReopeningMarksUnknownEndWithoutInventingTimestamp()
    {
        var file = NewDatabasePath();
        var mapping = Guid.NewGuid();
        var id = Guid.NewGuid();
        await using (var store = new ClientHistoryStore(file))
        {
            store.RecordConnected(mapping, id, "127.0.0.1:1234", DateTime.UtcNow);
            await store.FlushAsync();
        }
        await using var reopened = new ClientHistoryStore(file);
        var row = Assert.Single(await reopened.GetHistoryAsync(mapping));
        Assert.Null(row.DisconnectedAtUtc);
        Assert.Null(row.DurationMs);
        Assert.Equal("上次运行异常中断", row.DisconnectReason);
    }

    [Fact]
    public async Task PrunesToMostRecentThousandCompletedSessionsPerMapping()
    {
        var file = NewDatabasePath();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var start = DateTime.UtcNow.AddHours(-2);
        await using var store = new ClientHistoryStore(file);
        for (int i = 0; i < 1003; i++)
        {
            var id = Guid.NewGuid();
            var at = start.AddSeconds(i);
            store.RecordConnected(first, id, "10.0.0.1:5000", at);
            store.RecordClosed(first, id, at, at.AddSeconds(1), i, 1, "客户端主动断开");
        }
        var other = Guid.NewGuid();
        store.RecordConnected(second, other, "10.0.0.2:5000", start);
        store.RecordClosed(second, other, start, start.AddSeconds(1), 0, 0, "客户端主动断开");
        var a = await store.GetHistoryAsync(first);
        Assert.Equal(1000, a.Count);
        Assert.Equal(1002, a[0].SentBytes);
        Assert.Equal(3, a[^1].SentBytes);
        Assert.Single(await store.GetHistoryAsync(second));
    }

    [Fact]
    public async Task DeleteMappingDrainsPendingWritesAndPreservesOtherMappings()
    {
        var file = NewDatabasePath();
        var removed = Guid.NewGuid();
        var retained = Guid.NewGuid();
        await using var store = new ClientHistoryStore(file);
        var start = DateTime.UtcNow;
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        store.RecordConnected(removed, a, "removed", start);
        store.RecordConnected(retained, b, "retained", start);
        store.RecordClosed(removed, a, start, start.AddSeconds(1), 1, 2, "closed");
        store.RecordClosed(retained, b, start, start.AddSeconds(1), 3, 4, "closed");
        await store.DeleteMappingAsync(removed);
        // Late disconnection must not resurrect a deleted mapping.
        store.RecordClosed(removed, a, start, start.AddSeconds(2), 5, 6, "late");
        Assert.Empty(await store.GetHistoryAsync(removed));
        Assert.Single(await store.GetHistoryAsync(retained));
    }

    [Fact]
    public async Task DuplicateEndEventDoesNotOverwriteFirstReason()
    {
        var file = NewDatabasePath();
        var mapping = Guid.NewGuid();
        await using var store = new ClientHistoryStore(file);
        var start = DateTime.UtcNow;
        var id = Guid.NewGuid();
        store.RecordConnected(mapping, id, "192.168.0.5:42", start);
        store.RecordClosed(mapping, id, start, start.AddSeconds(1), 10, 5, "first");
        store.RecordClosed(mapping, id, start, start.AddSeconds(2), 15, 6, "duplicate");
        var row = Assert.Single(await store.GetHistoryAsync(mapping));
        Assert.Equal("first", row.DisconnectReason);
        Assert.Equal(10, row.SentBytes);
    }
    [Fact]
    public async Task RecoveredInterruptedSessionsCountTowardRetentionLimit()
    {
        var file = NewDatabasePath();
        var mapping = Guid.NewGuid();
        var start = DateTime.UtcNow.AddDays(-1);
        await using (var store = new ClientHistoryStore(file))
        {
            for (int i = 0; i < 1003; i++)
                store.RecordConnected(mapping, Guid.NewGuid(), "interrupted", start.AddSeconds(i));
            await store.FlushAsync();
        }

        await using var reopened = new ClientHistoryStore(file);
        var rows = await reopened.GetHistoryAsync(mapping);
        Assert.Equal(1000, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Null(row.DisconnectedAtUtc);
            Assert.Null(row.DurationMs);
            Assert.Equal("上次运行异常中断", row.DisconnectReason);
        });
    }

    [Fact]
    public async Task TcpServerStopFlushesFinalizedConnectionCounters()
    {
        var file = NewDatabasePath();
        var mapping = Guid.NewGuid();
        var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        await using var store = new ClientHistoryStore(file);
        var config = new MappingConfig
        {
            Id = mapping,
            PortName = "COM99",
            ListenIp = "127.0.0.1",
            ListenPort = port,
        };
        await using var server = new TcpServerService(config, store);
        server.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (server.ClientCount != 1 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Equal(1, server.ClientCount);

        var payload = new byte[32768];
        server.Broadcast(payload);
        var received = 0;
        while (received < payload.Length)
        {
            var buffer = new byte[8192];
            var n = await client.GetStream().ReadAsync(buffer).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(n > 0);
            received += n;
        }
        Assert.Equal(payload.Length, received);

        // The send loop may not have incremented SentBytes when the socket
        // returns the final bytes. StopAsync must wait for I/O before recording.
        await server.StopAsync();
        var row = Assert.Single(await store.GetHistoryAsync(mapping));
        Assert.Equal(payload.Length, row.SentBytes);
        Assert.Equal(0, row.ReceivedBytes);
        Assert.Equal("服务端停止监听", row.DisconnectReason);
        Assert.NotNull(row.DisconnectedAtUtc);
    }

    [Fact]
    public async Task ImmediateDisconnectsDoNotLoseSessionFinalization()
    {
        var file = NewDatabasePath();
        var mapping = Guid.NewGuid();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var store = new ClientHistoryStore(file);
        await using var server = new TcpServerService(new MappingConfig
        {
            Id = mapping, PortName = "COM99", ListenIp = "127.0.0.1",
            ListenPort = port, ClientMode = ClientMode.Multi, MaxClients = 64,
        }, store);
        server.Start();

        const int sessionCount = 32;
        // Rapid accepts and immediate disconnects exercise the transition
        // between task publication, RecvLoop and close finalization.
        for (var i = 0; i < sessionCount; i++)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            client.Close();
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (server.TotalAccepted < sessionCount && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Equal(sessionCount, server.TotalAccepted);

        await server.StopAsync();
        var records = await store.GetHistoryAsync(mapping);
        Assert.Equal(sessionCount, records.Count);
        Assert.All(records, row =>
        {
            Assert.NotNull(row.DisconnectedAtUtc);
            Assert.NotNull(row.DurationMs);
            Assert.NotNull(row.DisconnectReason);
        });
    }

}
