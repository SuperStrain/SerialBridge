using System.Net;
using System.Net.Sockets;
using System.Text;
using SerialBridge.Core.Bridge;
using SerialBridge.Core.Models;
using SerialBridge.Core.Serial;
using Xunit;
using Xunit.Abstractions;

namespace SerialBridge.Core.Tests;

/// <summary>
/// 桥接核心行为测试：用 FakePortFactory 模拟物理串口设备端，
/// 通过真实 TCP Socket 验证双向透明转发、连接管理与异常保护。
/// </summary>
public sealed class BridgeTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private FakePortFactory _factory = null!;
    private int _port;

    public BridgeTests(ITestOutputHelper output) => _output = output;

    public Task InitializeAsync()
    {
        Logging.LogService.Initialize(Path.Combine(Path.GetTempPath(), "sbtest-logs"));
        _factory = new FakePortFactory();
        _port = GetFreePort();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private MappingRuntime CreateRuntime(Action<MappingConfig>? setup = null)
    {
        var config = new MappingConfig
        {
            Name = "测试映射",
            PortName = "COM99",
            ListenIp = "127.0.0.1",
            ListenPort = _port,
            ReconnectIntervalMs = 500,
        };
        setup?.Invoke(config);
        return new MappingRuntime(config, _factory);
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static async Task<TcpClient> ConnectAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        client.NoDelay = true;
        return client;
    }

    private static async Task<byte[]> ReadAllAsync(TcpClient client, int expected, TimeSpan timeout)
    {
        var buffer = new byte[expected];
        var received = 0;
        var deadline = DateTime.UtcNow + timeout;
        var stream = client.GetStream();
        while (received < expected && DateTime.UtcNow < deadline)
        {
            if (!stream.DataAvailable)
            {
                await Task.Delay(10);
                continue;
            }
            var n = await stream.ReadAsync(buffer.AsMemory(received));
            if (n <= 0) break;
            received += n;
        }
        return buffer[..received];
    }

    // ---------- 测试用例 ----------

    [Fact]
    public async Task SerialToTcp_ForwardsDataInOrder()
    {
        await using var rt = CreateRuntime();
        await rt.StartAsync();
        await WaitSerialOpen(rt);
        var device = _factory.OpenedStreams.Single();

        using var client = await ConnectAsync(_port);
        await WaitUntil(() => rt.ClientCount == 1);

        for (var i = 0; i < 10; i++)
            await device.SimulateDeviceSendAsync(Encoding.ASCII.GetBytes($"MSG-{i:00};"));

        var data = await ReadAllAsync(client, 70, TimeSpan.FromSeconds(5));
        Assert.Equal("MSG-00;MSG-01;MSG-02;MSG-03;MSG-04;MSG-05;MSG-06;MSG-07;MSG-08;MSG-09;",
            Encoding.ASCII.GetString(data));
        Assert.Equal(70, rt.SerialRxBytes);
    }

    [Fact]
    public async Task TcpToSerial_ForwardsDataInOrder()
    {
        await using var rt = CreateRuntime();
        await rt.StartAsync();
        await WaitSerialOpen(rt);
        var device = _factory.OpenedStreams.Single();

        using var client = await ConnectAsync(_port);
        await WaitUntil(() => rt.ClientCount == 1);

        var stream = client.GetStream();
        var payload = new byte[] { 0x01, 0x00, 0xFF, 0xAB, 0x00, 0x7F };
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
        await WaitUntil(() => device.DataWrittenByBridge.Count >= 1);

        var written = device.DataWrittenByBridge.ToArray();
        Assert.Equal(payload, written[0]);
        Assert.Equal(6, rt.SerialTxBytes);
    }

    [Fact]
    public async Task BinaryDataWithZeros_IsPreserved()
    {
        await using var rt = CreateRuntime();
        await rt.StartAsync();
        await WaitSerialOpen(rt);
        var device = _factory.OpenedStreams.Single();

        using var client = await ConnectAsync(_port);
        await WaitUntil(() => rt.ClientCount == 1);

        // 0x00、0xFF、0x0D、0x0A 等敏感字节必须原样透传
        var payload = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        await device.SimulateDeviceSendAsync(payload);
        var data = await ReadAllAsync(client, payload.Length, TimeSpan.FromSeconds(5));
        Assert.Equal(payload, data);
    }

    [Fact]
    public async Task SingleClientMode_RejectsSecondClient()
    {
        await using var rt = CreateRuntime();
        await rt.StartAsync();
        await WaitSerialOpen(rt);

        using var first = await ConnectAsync(_port);
        await WaitUntil(() => rt.ClientCount == 1);

        using var second = await ConnectAsync(_port);
        // 第二个连接应被服务端立即关闭（对端关闭表现为可读且无数据）
        await WaitUntil(
            () => second.Client.Poll(100_000, SelectMode.SelectRead) && second.Available == 0,
            timeout: TimeSpan.FromSeconds(3));
        Assert.Equal(1, rt.ClientCount);
    }

    [Fact]
    public async Task MultiClientMode_BroadcastsToAllClients()
    {
        await using var rt = CreateRuntime(c => { c.ClientMode = ClientMode.Multi; c.MaxClients = 4; });
        await rt.StartAsync();
        await WaitSerialOpen(rt);
        var device = _factory.OpenedStreams.Single();

        using var c1 = await ConnectAsync(_port);
        using var c2 = await ConnectAsync(_port);
        using var c3 = await ConnectAsync(_port);
        await WaitUntil(() => rt.ClientCount == 3);

        await device.SimulateDeviceSendAsync(Encoding.ASCII.GetBytes("HELLO"));
        var d1 = await ReadAllAsync(c1, 5, TimeSpan.FromSeconds(5));
        var d2 = await ReadAllAsync(c2, 5, TimeSpan.FromSeconds(5));
        var d3 = await ReadAllAsync(c3, 5, TimeSpan.FromSeconds(5));
        Assert.All(new[] { d1, d2, d3 }, d => Assert.Equal("HELLO", Encoding.ASCII.GetString(d)));
    }

    [Fact]
    public async Task MultiClientMode_SerializesWritesFromDifferentClients()
    {
        await using var rt = CreateRuntime(c => { c.ClientMode = ClientMode.Multi; c.MaxClients = 4; });
        await rt.StartAsync();
        await WaitSerialOpen(rt);
        var device = _factory.OpenedStreams.Single();

        // 两个客户端并发各发 50 条消息，写入串口的数据必须每条完整不交叉
        var tasks = new List<Task>();
        for (var c = 0; c < 2; c++)
        {
            var clientIndex = c;
            tasks.Add(Task.Run(async () =>
            {
                using var client = await ConnectAsync(_port);
                var stream = client.GetStream();
                for (var i = 0; i < 50; i++)
                {
                    var msg = Encoding.ASCII.GetBytes($"[{clientIndex}]{i:000};");
                    await stream.WriteAsync(msg);
                }
            }));
        }
        await Task.WhenAll(tasks);
        await WaitUntil(() =>
        {
            lock (device.DataWrittenByBridge)
                return device.DataWrittenByBridge.Sum(b => b.Length) >= 700;
        }, TimeSpan.FromSeconds(10));
        await Task.Delay(300); // 等写队列完全排空

        var all = device.DataWrittenByBridge.ToArray();
        // TCP 字节流允许小消息合并到达；验证每个写入串口的块都是完整消息的组合，无字节交叉
        var combined = string.Concat(all.Select(Encoding.ASCII.GetString));
        Assert.Matches(@"^(?:\[\d\]\d{3};){100}$", combined);
        Assert.Equal(2, all.DistinctBy(c => Encoding.ASCII.GetString(c)[1]).Count() + 0);
    }

    [Fact]
    public async Task SlowClient_IsDisconnectedWhenBufferOverflows()
    {
        await using var rt = CreateRuntime();
        await rt.StartAsync();
        await WaitSerialOpen(rt);
        var device = _factory.OpenedStreams.Single();

        using var slow = await ConnectAsync(_port);
        await WaitUntil(() => rt.ClientCount == 1);

        // 持续灌数据且客户端从不读取；localhost 的 OS 缓冲可能吸收数十 MB，
        // 因此循环发送直到应用层发送队列溢出触发保护断开（上限约 80MB）
        var chunk = new byte[65536];
        for (var i = 0; i < 1280 && rt.ClientCount > 0; i++)
            await device.SimulateDeviceSendAsync(chunk);

        await WaitUntil(() => rt.ClientCount == 0, TimeSpan.FromSeconds(30));
        Assert.Equal(0, rt.ClientCount);
    }

    [Fact]
    public async Task TcpClientDisconnect_DoesNotBreakMapping()
    {
        await using var rt = CreateRuntime();
        await rt.StartAsync();
        await WaitSerialOpen(rt);
        var device = _factory.OpenedStreams.Single();

        var client1 = await ConnectAsync(_port);
        await WaitUntil(() => rt.ClientCount == 1);
        client1.Close();
        await WaitUntil(() => rt.ClientCount == 0);

        // 断开重连后继续正常通信
        using var client2 = await ConnectAsync(_port);
        await WaitUntil(() => rt.ClientCount == 1);
        var stream = client2.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("AFTER-RECONNECT"));
        await WaitUntil(() => device.DataWrittenByBridge.Count >= 1);
        Assert.Equal("AFTER-RECONNECT",
            Encoding.ASCII.GetString(device.DataWrittenByBridge.First()));
    }

    [Fact]
    public async Task SerialReconnects_AfterDeviceReopens()
    {
        await using var rt = CreateRuntime();
        await rt.StartAsync();
        await WaitSerialOpen(rt);
        var device1 = _factory.OpenedStreams.Single();

        // 模拟 USB 拔出：设备端流销毁（读会抛异常）
        device1.Dispose();
        await WaitUntil(() => rt.Serial.State == SerialState.ReconnectWaiting, TimeSpan.FromSeconds(5));

        // 新流可用（重新插入），等待自动重连成功
        await WaitUntil(() => rt.Serial.State == SerialState.Open, TimeSpan.FromSeconds(10));
        Assert.Equal(2, _factory.OpenedStreams.Count);
    }

    [Fact]
    public async Task PortBusy_StopsRetryAndReportsFault()
    {
        _factory.OpenFailure = _ => new PortOpenException(PortOpenError.AccessDenied, "串口被其他程序占用");
        await using var rt = CreateRuntime();
        await rt.StartAsync();
        await WaitSerialOpen(rt);

        await WaitUntil(() => rt.Serial.State == SerialState.Faulted, TimeSpan.FromSeconds(5));
        // TCP 监听仍然正常
        Assert.True(rt.Tcp.IsListening);
    }

    [Fact]
    public async Task TcpPortConflict_FailsWithFriendlyMessage()
    {
        // 先占用端口
        var blocker = new System.Net.Sockets.TcpListener(IPAddress.Loopback, _port);
        blocker.Start();
        try
        {
            await using var rt = CreateRuntime();
            await rt.StartAsync();
        await WaitSerialOpen(rt);
            await WaitSerialOpen(rt);
            Assert.Equal(MappingState.Faulted, rt.State);
            Assert.Contains("占用", rt.FaultMessage);
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public async Task StopReleasesAllResources()
    {
        var rt = CreateRuntime();
        await rt.StartAsync();
        await WaitSerialOpen(rt);
        using var client = await ConnectAsync(_port);
        await WaitUntil(() => rt.ClientCount == 1);
        Assert.True(_factory.OpenedStreams.Single().CanRead);

        await rt.StopAsync();
        Assert.Equal(MappingState.Stopped, rt.State);
        Assert.False(rt.Tcp.IsListening);

        // 端口已释放：重新绑定应成功，而新连接应被拒绝（无监听者）
        var rebinder = new TcpListener(IPAddress.Loopback, _port);
        rebinder.Start();
        rebinder.Stop();
        await Assert.ThrowsAnyAsync<SocketException>(() => ConnectAsync(_port));
    }


    [Fact]
    public async Task SingleClient_KeepsByteOrderOnBurstWrites()
    {
        // 模拟交互式终端场景（如 Tab 补全）：小数据块高频到达，
        // 设备端收到的字节顺序必须与客户端发送顺序完全一致
        await using var rt = CreateRuntime();
        await rt.StartAsync();
        await WaitSerialOpen(rt);
        var device = _factory.OpenedStreams.Single();

        using var client = await ConnectAsync(_port);
        await WaitUntil(() => rt.ClientCount == 1);

        var stream = client.GetStream();
        var sent = new List<byte>();
        for (var round = 0; round < 50; round++)
        {
            // 模拟 "ifc	"：一批 4 字节后紧跟下一批，块间不等待
            var batch = new byte[] { (byte)'i', (byte)'f', (byte)'c', 0x09, (byte)round };
            await stream.WriteAsync(batch);
            sent.AddRange(batch);
        }
        await WaitUntil(() =>
        {
            lock (device.DataWrittenByBridge)
                return device.DataWrittenByBridge.Sum(b => b.Length) >= sent.Count;
        }, TimeSpan.FromSeconds(10));
        await Task.Delay(300); // 等写队列完全排空

        var all = device.DataWrittenByBridge.ToArray();
        var combined = all.SelectMany(b => b).ToArray();
        Assert.Equal(sent, combined);
    }


    [Fact]
    public async Task RestartMapping_AcceptsClientsAgain()
    {
        // 回归：映射停止再启动后，TCP 必须能再次接受客户端并透传
        await using var rt = CreateRuntime();
        await rt.StartAsync();
        await WaitSerialOpen(rt);
        var device = _factory.OpenedStreams.First();

        using (var c1 = await ConnectAsync(_port))
        {
            await WaitUntil(() => rt.ClientCount == 1);
            await c1.GetStream().WriteAsync(new byte[] { 0x01 });
            await WaitUntil(() => device.DataWrittenByBridge.Count >= 1);
        }
        await WaitUntil(() => rt.ClientCount == 0);

        await rt.StopAsync();
        await rt.StartAsync();                    // 第二次启动
        await WaitSerialOpen(rt);

        using (var c2 = await ConnectAsync(_port))
        {
            await WaitUntil(() => rt.ClientCount == 1, TimeSpan.FromSeconds(5));
            await c2.GetStream().WriteAsync(new byte[] { 0x02 });
            await WaitUntil(() => rt.SerialTxBytes >= 2, TimeSpan.FromSeconds(5));
        }
    }

    // ---------- 辅助 ----------

    /// <summary>等待串口打开（FakePortFactory 打开总是成功；Faulted 时直接返回供故障类测试使用）。</summary>
    private static async Task WaitSerialOpen(MappingRuntime rt)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (rt.Serial.State is SerialState.Open or SerialState.Faulted) return;
            await Task.Delay(25);
        }
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        Assert.True(condition(), $"等待条件超时（{(timeout ?? TimeSpan.FromSeconds(5)).TotalSeconds}s）");
    }
}
