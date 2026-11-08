using System.IO;
using System.Text;
using SerialBridge.Core.Bridge;
using SerialBridge.Core.Models;
using SerialBridge.Core.Serial;
using Xunit;

namespace SerialBridge.Core.Tests;

/// <summary>
/// 命令预设发送器测试：经真实 MappingRuntime + FakePortFactory 验证
/// 预检失败、顺序发送与串口断开后的永久中止语义（终端命令栏与命令面板共用此路径）。
/// </summary>
public sealed class CommandPresetSenderTests
{
    private readonly FakePortFactory _factory = new();
    private readonly int _port;

    public CommandPresetSenderTests()
    {
        Logging.LogService.Initialize(Path.Combine(Path.GetTempPath(), "sbtest-logs"));
        _port = GetFreePort();
    }

    private MappingRuntime CreateRuntime() => new(new MappingConfig
    {
        Name = "预设测试",
        PortName = "COM99",
        ListenIp = "127.0.0.1",
        ListenPort = _port,
        ReconnectIntervalMs = 500,
    }, _factory);

    private static CommandPreset Preset(string name, params CommandStep[] steps)
    {
        var preset = new CommandPreset { Name = name };
        preset.Steps.Clear();
        preset.Steps.AddRange(steps);
        return preset;
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitSerialOpen(MappingRuntime runtime)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (runtime.Serial.State is SerialState.Open or SerialState.Faulted) return;
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

    [Fact]
    public async Task SendAsync_WhileMappingStopped_ThrowsPrecheckError()
    {
        await using var runtime = CreateRuntime();
        var preset = Preset("cmd", new CommandStep { Content = "ls" });
        await Assert.ThrowsAsync<IOException>(() => CommandPresetSender.SendAsync(runtime, preset));
        Assert.Empty(_factory.OpenedStreams);
    }

    [Fact]
    public async Task SendAsync_SendsStepsInOrderThroughRuntimeQueue()
    {
        await using var runtime = CreateRuntime();
        await runtime.StartAsync();
        await WaitSerialOpen(runtime);
        var device = _factory.OpenedStreams.Single();

        var preset = Preset("两步宏",
            new CommandStep { Format = "Text", Content = "echo one", Ending = "CR", DelayMs = 0 },
            new CommandStep { Format = "Text", Content = "echo two", Ending = "CRLF", DelayMs = 0 });

        await CommandPresetSender.SendAsync(runtime, preset);

        await WaitUntil(() => device.DataWrittenByBridge.Count >= 2);
        Assert.Equal("echo one\r", Encoding.UTF8.GetString(device.DataWrittenByBridge.ElementAt(0)));
        Assert.Equal("echo two\r\n", Encoding.UTF8.GetString(device.DataWrittenByBridge.ElementAt(1)));
    }

    [Fact]
    public async Task SendAsync_InvalidStep_FailsBeforeSendingAnything()
    {
        await using var runtime = CreateRuntime();
        await runtime.StartAsync();
        await WaitSerialOpen(runtime);
        var device = _factory.OpenedStreams.Single();

        var preset = Preset("坏宏",
            new CommandStep { Format = "Text", Content = "echo one", Ending = "CR" },
            new CommandStep { Format = "Hex", Content = "ZZ", Ending = "None" });

        await Assert.ThrowsAsync<FormatException>(() => CommandPresetSender.SendAsync(runtime, preset));
        Assert.Empty(device.DataWrittenByBridge);
    }

    [Fact]
    public async Task SendAsync_DisconnectDuringDelay_AbortsEvenAfterReconnect()
    {
        await using var runtime = CreateRuntime();
        await runtime.StartAsync();
        await WaitSerialOpen(runtime);
        var device = _factory.OpenedStreams.Single();

        var preset = Preset("中断宏",
            new CommandStep { Format = "Text", Content = "one", Ending = "None", DelayMs = 5000 },
            new CommandStep { Format = "Text", Content = "two", Ending = "None" });

        var run = CommandPresetSender.SendAsync(runtime, preset);

        // 第一步写入串口后立即“拔出”设备，状态离开 Open 应触发永久中止
        await WaitUntil(() => device.DataWrittenByBridge.Count >= 1);
        device.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        // 自动重连完成后，第二步也不允许再发送
        await WaitUntil(() => _factory.OpenedStreams.Count >= 2);
        await Task.Delay(200);
        Assert.Single(device.DataWrittenByBridge);
    }
}
