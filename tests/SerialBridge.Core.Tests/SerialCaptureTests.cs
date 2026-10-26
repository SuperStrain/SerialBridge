using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using SerialBridge.Core.Bridge;
using SerialBridge.Core.Capture;
using SerialBridge.Core.Models;

namespace SerialBridge.Core.Tests;

/// <summary>
/// 串口日志捕获服务测试：注入固定时间源，直接驱动写任务，
/// 校验文件内容、编码、轮转、清理与降级行为。
/// </summary>
public sealed class SerialCaptureTests : IDisposable
{
    private readonly string _dir;

    public SerialCaptureTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sbcapture", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private SerialCaptureOptions Options(string? dir = null, DataLogFormat format = DataLogFormat.Text,
        DataLogTextEncoding encoding = DataLogTextEncoding.Utf8, int maxFileMB = 50,
        int retention = 30, Func<DateTime>? clock = null) => new()
    {
        Directory = dir ?? _dir,
        MappingName = "测试映射",
        Format = format,
        TextEncoding = encoding,
        RetentionDays = retention,
        MaxFileBytes = (long)maxFileMB * 1024 * 1024,
        Clock = clock ?? (() => new DateTime(2026, 10, 11, 12, 0, 0)),
    };


    [Fact]
    public async Task TextUtf8_HighFrequencyMultiSegment_AllDataPersisted()
    {
        // 复现：高频小块 + 多行混合（模拟海思板输出模式），写线程不得崩溃、数据不中断
        var rnd = new Random(42);
        await RunCaptureAsync(Options(), svc =>
        {
            for (var i = 0; i < 300; i++)
            {
                var len = rnd.Next(1, 200);
                var buf = new byte[len];
                for (var j = 0; j < len; j++)
                    buf[j] = (byte)(rnd.Next(4) == 0 ? (rnd.Next(2) == 0 ? '\n' : '\r') : ('a' + rnd.Next(26)));
                svc.Enqueue(DataDirection.SerialToNet, buf);
            }
        }, TimeSpan.FromMilliseconds(1200));

        var file = WaitForFile(_dir);
        var content = File.ReadAllText(file);

        // 每一行（按 \n 分隔）都应带时间戳前缀
        var lines = content.Split('\n');
        Assert.True(lines.Length > 50, $"行数 {lines.Length} 应大于 50");
        foreach (var line in lines)
        {
            // 跳过行首行界符（上一行数据末尾的 CR 会附着在按 LF 分割的下一行开头）与空行
            var body = line.TrimStart('\r', '\n');
            if (body.Length == 0) continue;
            Assert.True(body.StartsWith('['),
                $"行缺少时间戳前缀: {System.Text.Json.JsonSerializer.Serialize(line[..Math.Min(40, line.Length)])}");
        }

        Assert.DoesNotContain("\uFFFD", content);
    }

    private static string WaitForFile(string dir, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.log") : Array.Empty<string>();
            if (files.Length > 0)
            {
                // 等写线程 flush 稳定
                Thread.Sleep(150);
                return files[0];
            }
            Thread.Sleep(25);
        }
        throw new TimeoutException("日志文件未生成");
    }

    private static async Task RunCaptureAsync(SerialCaptureOptions opt,
        Action<SerialCaptureService> act, TimeSpan runFor)
    {
        using var svc = new SerialCaptureService(opt);
        svc.Start();
        act(svc);
        await Task.Delay(runFor);
        await Task.Run(() => svc.Dispose());
    }

    [Fact]
    public async Task TextUtf8_DeviceOutput_WrittenVerbatim()
    {
        var payload = "Hello 中文设备输出\r\n第二行 0x00 之后不应出现替换符问题——UTF-8 原样写入"u8.ToArray();
        await RunCaptureAsync(Options(), svc =>
        {
            svc.Enqueue(DataDirection.SerialToNet, payload);
        }, TimeSpan.FromMilliseconds(600));

        var file = WaitForFile(_dir);
        var content = File.ReadAllBytes(file);
        // UTF-8 原样写入：payload 字节必须逐字节出现在文件中（含中文与换行）
        var asSet = new HashSet<byte>(content);
        Assert.True(payload.All(b => asSet.Contains(b)), "payload 字节应全部存在于文件");
        Assert.DoesNotContain("\uFFFD", Encoding.UTF8.GetString(content));
    }

    [Fact]
    public async Task TextUtf8_InboundNotWritten()
    {
        await RunCaptureAsync(Options(), svc =>
        {
            svc.Enqueue(DataDirection.SerialToNet, "OUT-DATA"u8.ToArray());
            svc.Enqueue(DataDirection.NetToSerial, "IN-DATA"u8.ToArray());   // 文本模式不记录下发
        }, TimeSpan.FromMilliseconds(600));

        var content = File.ReadAllText(WaitForFile(_dir));
        Assert.Contains("OUT-DATA", content);
        Assert.DoesNotContain("IN-DATA", content);
        Assert.Contains("[2026-10-11 12:00:00.000] OUT-DATA", content);   // 行首时间戳
    }

    [Fact]
    public async Task TextGbk_DeviceBytesDecoded()
    {
        // 模拟 GBK 设备："中文测试" 的 GBK 编码字节
        Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var gbkBytes = Encoding.GetEncoding("GBK").GetBytes("中文测试");
        await RunCaptureAsync(Options(encoding: DataLogTextEncoding.Gbk), svc =>
        {
            svc.Enqueue(DataDirection.SerialToNet, gbkBytes);
        }, TimeSpan.FromMilliseconds(600));

        var content = File.ReadAllText(WaitForFile(_dir));   // UTF-8 读取
        Assert.Contains("中文测试", content);                 // 已转 UTF-8 且内容正确
        Assert.Contains("[2026-10-11 12:00:00.000] ", content);  // 行首时间戳前缀
    }

    [Fact]
    public async Task HexMode_BothDirections_WithMarkers()
    {
        await RunCaptureAsync(Options(format: DataLogFormat.Hex), svc =>
        {
            svc.Enqueue(DataDirection.SerialToNet, "HELLO"u8.ToArray());
            svc.Enqueue(DataDirection.NetToSerial, "WORLD"u8.ToArray());
        }, TimeSpan.FromMilliseconds(600));

        var content = File.ReadAllText(WaitForFile(_dir));
        Assert.Contains(" OUT ", content);
        Assert.Contains(" IN ", content);
        Assert.Contains("48 45 4C 4C 4F", content);          // HELLO
        Assert.Contains("57 4F 52 4C 44", content);          // WORLD
        Assert.Contains("2026-10-11 12:00:00", content);      // 注入时间源
    }

    [Fact]
    public async Task HexMode_ByDayRotation()
    {
        var day1 = new DateTime(2026, 10, 11, 23, 59, 58);
        var day2 = day1.AddDays(1);
        var clock = day1;
        await RunCaptureAsync(Options(format: DataLogFormat.Hex, clock: () => clock), svc =>
        {
            svc.Enqueue(DataDirection.SerialToNet, "DAY1"u8.ToArray());
            clock = day2;                                     // 时间推进跨天
            svc.Enqueue(DataDirection.SerialToNet, "DAY2"u8.ToArray());
        }, TimeSpan.FromMilliseconds(600));

        Assert.Equal(2, Directory.GetFiles(_dir, "*.log").Length);   // 两天两文件
        var files = Directory.GetFiles(_dir, "*.log");
        Assert.Contains(files, f => File.ReadAllText(f).Contains("DAY1"));
        Assert.Contains(files, f => File.ReadAllText(f).Contains("DAY2"));
    }

    [Fact]
    public async Task HexMode_SizeRotation()
    {
        // 极小文件上限触发同日分卷
        await RunCaptureAsync(Options(format: DataLogFormat.Hex, maxFileMB: 0), svc =>
        {
            for (var i = 0; i < 40; i++)
                svc.Enqueue(DataDirection.SerialToNet, new byte[1024]);   // 40KB > 内部最小分卷
        }, TimeSpan.FromMilliseconds(900));
        // maxFileMB=0 时 MaxFileBytes=0 → 每块都轮转；断言产生多个分卷文件
        var count = Directory.GetFiles(_dir, "*.log").Length;
        Assert.True(count >= 2, $"分卷数 {count} 应 >= 2");
    }

    [Fact]
    public async Task Cleanup_RemovesExpiredFiles()
    {
        var clockDate = new DateTime(2026, 10, 11, 12, 0, 0);
        // 手工放置一个 40 天前的旧日志
        Directory.CreateDirectory(_dir);
        var oldFile = Path.Combine(_dir, "测试映射_20260901.log");
        File.WriteAllText(oldFile, "old");
        File.SetLastWriteTime(oldFile, clockDate.AddDays(-40));

        var now = clockDate;
        await RunCaptureAsync(Options(retention: 30, clock: () => now), svc =>
        {
            svc.Enqueue(DataDirection.SerialToNet, "NEW"u8.ToArray());
            now = now.AddDays(1);                              // 触发次日清理
            svc.Enqueue(DataDirection.SerialToNet, "NEW2"u8.ToArray());
        }, TimeSpan.FromMilliseconds(800));

        Assert.False(File.Exists(oldFile), "过期文件应被清理");
    }

    [Fact]
    public async Task DisabledMapping_NoCaptureInstance()
    {
        // 集成层面：Enabled=false 的映射运行时不创建 Capture
        var config = new MappingConfig
        {
            Name = "t",
            ListenIp = "127.0.0.1",
            DataLog = new DataLogConfig { Enabled = false, Directory = _dir },
        };
        await using var rt = new SerialBridge.Core.Bridge.MappingRuntime(config, new FakePortFactory());
        await rt.StartAsync();
        await rt.StopAsync();
        Assert.Null(rt.Capture);
    }
    [Fact]
    public void QueueFull_ReportsDroppedBlocksAndBytes()
    {
        var opt = Options();
        opt = new SerialCaptureOptions
        {
            Directory = opt.Directory, MappingName = opt.MappingName,
            Clock = opt.Clock, QueueCapacity = 1, MinFreeDiskBytes = 0
        };
        using var svc = new SerialCaptureService(opt);
        // No writer started: deterministic queue overflow.
        svc.Enqueue(DataDirection.SerialToNet, new byte[] { 1 });
        svc.Enqueue(DataDirection.SerialToNet, new byte[] { 2, 3 });
        Assert.Equal(1, svc.DroppedBlocks);
        Assert.Equal(2, svc.DroppedBytes);
    }

    [Fact]
    public async Task Stop_DrainsAcceptedEntries()
    {
        using (var svc = new SerialCaptureService(Options()))
        {
            svc.Start();
            for (int i = 0; i < 100; i++)
                svc.Enqueue(DataDirection.SerialToNet, Encoding.ASCII.GetBytes($"line-{i:D3}\\n"));
        }
        var body = File.ReadAllText(WaitForFile(_dir));
        Assert.Contains("line-000", body);
        Assert.Contains("line-099", body);
    }

    [Fact]
    public async Task PeriodicFlushFailure_ReportsFaultAndResumesAfterCooldown()
    {
        using var svc = new SerialCaptureService(Options());
        using var failedWriter = new FlushFailingWriter();
        // Install only before Start: fault the disk boundary without adding a test API
        // or racing the background writer's ownership of its stream.
        typeof(SerialCaptureService).GetField("_writer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(svc, failedWriter);
        var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.StateChanged += () =>
        {
            if (svc.State == CaptureState.Faulted) faulted.TrySetResult();
            if (svc.State == CaptureState.Recording && faulted.Task.IsCompleted) recovered.TrySetResult();
        };

        svc.Start();
        // Empty queue: only the periodic flush can hit this injected failure.
        await failedWriter.FlushAttempted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await faulted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(CaptureState.Faulted, svc.State);
        Assert.Contains("simulated disk flush failure", svc.StateReason);
        Assert.False(failedWriter.BaseStream.CanWrite);

        svc.Enqueue(DataDirection.SerialToNet, "during-fault"u8.ToArray());
        Assert.Equal(1, svc.DroppedBlocks);
        Assert.Equal(12, svc.DroppedBytes);
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(35));
        Assert.Null(svc.StateReason);
        svc.Enqueue(DataDirection.SerialToNet, "after-recovery"u8.ToArray());
        svc.Dispose();

        var body = File.ReadAllText(WaitForFile(_dir));
        Assert.Contains("after-recovery", body);
        Assert.DoesNotContain("during-fault", body);
    }

    [Fact]
    public async Task CaptureFault_DoesNotInterruptBidirectionalForwarding()
    {
        Directory.CreateDirectory(_dir);
        var invalidDirectory = Path.Combine(_dir, "file-instead-of-directory");
        File.WriteAllText(invalidDirectory, "occupied");
        var factory = new FakePortFactory();
        var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        await using var runtime = new MappingRuntime(new MappingConfig
        {
            Name = "capture-fault",
            ListenIp = "127.0.0.1",
            ListenPort = port,
            DataLog = new DataLogConfig { Enabled = true, Directory = invalidDirectory, Format = DataLogFormat.Hex },
        }, factory);
        await runtime.StartAsync();
        await WaitUntilAsync(() => runtime.Serial.IsOpen);
        Assert.NotNull(runtime.Capture);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await WaitUntilAsync(() => runtime.ClientCount == 1);
        var device = factory.OpenedStreams.Single();
        var payload = new byte[] { 0, 255, 13, 10, 1, 128 };

        await device.SimulateDeviceSendAsync(payload);
        var received = new byte[payload.Length];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.GetStream().ReadExactlyAsync(received, timeout.Token);
        Assert.Equal(payload, received);

        await client.GetStream().WriteAsync(payload, timeout.Token);
        await WaitUntilAsync(() => device.DataWrittenByBridge.Sum(block => block.Length) == payload.Length);
        Assert.Equal(payload, device.DataWrittenByBridge.SelectMany(block => block).ToArray());
        Assert.Equal(MappingState.Running, runtime.State);
        Assert.True(runtime.Capture.DroppedBlocks >= 2);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class FlushFailingWriter : StreamWriter
    {
        public TaskCompletionSource FlushAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FlushFailingWriter() : base(new MemoryStream()) { }

        public override void Flush() => throw new IOException("simulated disk flush failure");

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushAttempted.TrySetResult();
            return Task.FromException(new IOException("simulated disk flush failure"));
        }
    }

    [Fact]
    public async Task GbkCharacterSplitAcrossBlocks_Preserved()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding("GBK").GetBytes("中文");
        using (var svc = new SerialCaptureService(Options(encoding: DataLogTextEncoding.Gbk)))
        {
            svc.Start();
            foreach (byte b in bytes)
                svc.Enqueue(DataDirection.SerialToNet, new[] { b });
        }
        Assert.Contains("中文", File.ReadAllText(WaitForFile(_dir)));
    }

    [Fact]
    public void ExistingFullVolume_OnRestart_UsesNextVolume()
    {
        Directory.CreateDirectory(_dir);
        var first = Path.Combine(_dir, "测试映射_20261011.log");
        File.WriteAllText(first, "old-content");
        var options = new SerialCaptureOptions
        {
            Directory = _dir, MappingName = "测试映射",
            Clock = () => new DateTime(2026, 10, 11, 12, 0, 0),
            MaxFileBytes = 4, MinFreeDiskBytes = 0
        };
        using (var svc = new SerialCaptureService(options))
        {
            svc.Start();
            svc.Enqueue(DataDirection.SerialToNet, "new-content"u8.ToArray());
        }
        Assert.Equal("old-content", File.ReadAllText(first));
        Assert.True(File.Exists(Path.Combine(_dir, "测试映射_20261011_002.log")));
    }

}
