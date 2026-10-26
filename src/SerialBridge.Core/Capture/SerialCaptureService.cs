using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Channels;
using SerialBridge.Core.Logging;
using SerialBridge.Core.Models;

namespace SerialBridge.Core.Capture;

/// <summary>串口日志捕获状态（UI 绑定）。</summary>
public enum CaptureState
{
    Recording,      // 正常记录
    DiskShortage,   // 磁盘余量不足，暂停记录
    Faulted,        // IO 异常，重试中
}

/// <summary>串口日志捕获配置（可注入时间源/磁盘阈值以支持测试）。</summary>
public sealed class SerialCaptureOptions
{
    public required string Directory { get; init; }
    public required string MappingName { get; init; }
    public DataLogFormat Format { get; init; } = DataLogFormat.Text;
    public DataLogTextEncoding TextEncoding { get; init; } = DataLogTextEncoding.Utf8;
    public int RetentionDays { get; init; } = 30;
    public long MaxFileBytes { get; init; } = 50L * 1024 * 1024;
    public long MinFreeDiskBytes { get; init; } = 500L * 1024 * 1024;
    public int QueueCapacity { get; init; } = 2048;
    public Func<DateTime> Clock { get; init; } = () => DateTime.Now;
}

/// <summary>
/// 串口日志捕获服务：把映射的双向数据流异步落盘。
/// - 文本模式：仅 OUT（设备输出，含用户输入的回显，与 Xshell 日志一致），
///   UTF-8 原样写 / GBK 解码转 UTF-8
/// - HEX 模式：OUT+IN 双向，带时间戳、方向、字节数
/// - 按天 + 按大小轮转；保留期清理；磁盘余量/IO 异常降级（暂停记录、保护通信主链路）
/// </summary>
public sealed class SerialCaptureService : IDisposable
{
    static SerialCaptureService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);   // GBK 支持
    }

    private readonly SerialCaptureOptions _opt;
    private readonly Channel<CaptureEntry> _queue;
    private readonly CancellationTokenSource _cts = new();
    private Task? _writerTask;
    private StreamWriter? _writer;
    private string? _currentFile;
    private long _currentFileSize;
    private int _currentDate;                 // yyyyMMdd 整数
    private int _currentVolume;               // 同日分卷号
    private int _lastCleanupDate;
    private bool _lineStart = true;   // 写入位置是否处于行首（用于按行注入时间戳）
    private volatile CaptureState _state = CaptureState.Recording;
    private string? _stateReason;
    private long _droppedBlocks;
    private long _droppedBytes;
    private readonly Decoder _gbkDecoder = Encoding.GetEncoding("GBK").GetDecoder();
    private bool _stopping;

    public CaptureState State => _state;
    public string? StateReason { get; private set; }
    public string? CurrentFile { get; private set; }
    public long DroppedBlocks => Interlocked.Read(ref _droppedBlocks);
    public long DroppedBytes => Interlocked.Read(ref _droppedBytes);

    /// <summary>状态变化（后台线程触发；UI 侧自行调度）。</summary>
    public event Action? StateChanged;

    public SerialCaptureService(SerialCaptureOptions options)
    {
        _opt = options;
        _queue = Channel.CreateBounded<CaptureEntry>(new BoundedChannelOptions(_opt.QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,   // TryWrite false on full; never silently drop
        });
    }

    public void Start()
    {
        try
        {
            Directory.CreateDirectory(_opt.Directory);
        }
        catch (Exception ex)
        {
            SetState(CaptureState.Faulted, $"日志目录创建失败：{ex.Message}");
        }
        _writerTask = Task.Run(() => WriterLoopAsync(_cts.Token));
    }

    /// <summary>
    /// 数据入队（调用方传入的 data 引用会被复制，不共享）。
    /// 队列满时丢弃最旧未写块（保护通信主链路优先）。
    /// </summary>
    public void Enqueue(DataDirection direction, byte[] data)
    {
        if (_stopping || State != CaptureState.Recording)
        {
            CountDropped(data.Length);
            return;
        }
        // Text capture is device output only; do not waste queue capacity on IN.
        if (_opt.Format == DataLogFormat.Text && direction != DataDirection.SerialToNet) return;
        var copy = (byte[])data.Clone();
        if (!_queue.Writer.TryWrite(new CaptureEntry(_opt.Clock(), direction, copy)))
            CountDropped(copy.Length);
    }

    private void CountDropped(int length)
    {
        Interlocked.Increment(ref _droppedBlocks);
        Interlocked.Add(ref _droppedBytes, length);
    }

    public void Dispose()
    {
        _stopping = true;
        _queue.Writer.TryComplete();
        try
        {
            // Normal stop drains accepted items instead of immediately canceling.
            if (_writerTask is { } task && !task.Wait(TimeSpan.FromSeconds(5)))
            {
                LogX.Warn("串口日志停止超时，取消未完成写入");
                _cts.Cancel();
                task.Wait(TimeSpan.FromSeconds(2));
            }
        }
        catch (AggregateException ex) { LogX.Warn($"串口日志停止异常：{ex.GetBaseException().Message}"); }
        if (_writerTask is null || _writerTask.IsCompleted) _cts.Dispose();
    }

    // ---------- 内部实现 ----------

    private readonly record struct CaptureEntry(DateTime Time, DataDirection Direction, byte[] Data);

    private async Task WriterLoopAsync(CancellationToken ct)
    {
        // 落盘节奏：高流量时每 64KB flush；无论如何每 500ms flush 一次，
        // 保证低流量会话的数据最坏 0.5 秒内落盘（断电/崩溃最多丢最近半秒）
        var faultCooldownUntil = DateTime.MinValue;
        void HandleWriteFault(Exception ex)
        {
            CloseWriter();
            SetState(CaptureState.Faulted, $"写入失败：{ex.Message}（每 30 秒自动重试）");
            LogX.Error($"串口日志写入异常堆栈：{ex}", ex);
            faultCooldownUntil = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        }
        try
        {
            while (true)
            {
                // A timed wait doesn't leave abandoned channel read waiters.
                using var poll = CancellationTokenSource.CreateLinkedTokenSource(ct);
                poll.CancelAfter(TimeSpan.FromMilliseconds(500));
                bool available;
                try { available = await _queue.Reader.WaitToReadAsync(poll.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { available = false; }
                if (ct.IsCancellationRequested) break;

                if (State == CaptureState.DiskShortage && DiskHasSpace())
                    SetState(CaptureState.Recording, null);
                if (State == CaptureState.Faulted && DateTime.UtcNow >= faultCooldownUntil)
                    SetState(CaptureState.Recording, null);

                if (available)
                {
                    while (_queue.Reader.TryRead(out var e))
                    {
                        if (State != CaptureState.Recording)
                        {
                            CountDropped(e.Data.Length);
                            continue;
                        }
                        try
                        {
                            await WriteEntryAsync(e, ct);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
                        {
                            CountDropped(e.Data.Length);
                            HandleWriteFault(ex);
                            break;
                        }
                    }
                }
                try
                {
                    if (_writer is not null) await _writer.FlushAsync(CancellationToken.None);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
                {
                    HandleWriteFault(ex);
                }
                if (_queue.Reader.Completion.IsCompleted) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogX.Error($"串口日志写线程异常终止：{ex.Message}", ex);
        }
        finally
        {
            CloseWriter();
        }
    }

    private async Task WriteEntryAsync(CaptureEntry e, CancellationToken ct)
    {
        EnsureWriter(e.Time);

        if (_writer is null) { CountDropped(e.Data.Length); return; }

        if (_opt.Format == DataLogFormat.Text)
        {
            if (e.Direction != DataDirection.SerialToNet) return;   // 文本模式仅 OUT
            await WriteTextWithTimestampsAsync(e, ct);
        }
        else
        {
            var hexBytes = new StringBuilder(e.Data.Length * 3);
            for (var i = 0; i < e.Data.Length; i++)
            {
                if (i > 0) hexBytes.Append(' ');
                hexBytes.Append(e.Data[i].ToString("X2"));
            }
            var hex = hexBytes.ToString();
            var ascii = ToAscii(e.Data);
            var line = $"{e.Time:yyyy-MM-dd HH:mm:ss.fff} {(e.Direction == DataDirection.SerialToNet ? "OUT" : "IN ")} {e.Data.Length,5} {hex}{(ascii.Length > 0 ? " | " + ascii : "")}{Environment.NewLine}";
            await _writer.WriteAsync(line);
            _currentFileSize += Encoding.UTF8.GetByteCount(line);
        }

        // 批量 flush：缓冲超过 64KB 时落盘
        if (_currentFileSize % 65536 < 256)
            await _writer.FlushAsync(ct);

        // 每日清理（每天首次写盘时执行）
        var today = e.Time.Year * 10000 + e.Time.Month * 100 + e.Time.Day;
        if (_lastCleanupDate != today)
        {
            _lastCleanupDate = today;
            CleanupExpired();
        }
    }

    /// <summary>
    /// 文本模式写入：按行注入时间戳前缀（[yyyy-MM-dd HH:mm:ss.fff] ），
    /// 正文内容保持原样（UTF-8 原始字节 / GBK 转码）。CR 与 LF 均视为行界，
    /// 行界符原样写并置行首标志，确保下一行（无论来自哪个块）都带前缀。
    /// </summary>
    private async Task WriteTextWithTimestampsAsync(CaptureEntry e, CancellationToken ct)
    {
        var stream = _writer!.BaseStream;
        var start = 0;
        for (var i = 0; i < e.Data.Length; i++)
        {
            if (e.Data[i] is (byte)'\n' or (byte)'\r')
            {
                if (i > start)
                    await WriteSegmentAsync(stream, e.Data, start, i - start, ct);
                await stream.WriteAsync(new byte[] { e.Data[i] }, ct);
                _currentFileSize += 1;
                _lineStart = true;   // 行界 → 下一内容字符处于新行行首
                start = i + 1;
            }
        }
        if (start < e.Data.Length)
        {
            await WriteSegmentAsync(stream, e.Data, start, e.Data.Length - start, ct);
            _lineStart = false;
        }
    }

    /// <summary>写入一行内容段（不含行界符）：行首注入时间戳，正文按编码处理。</summary>
    private async Task WriteSegmentAsync(Stream stream, byte[] data, int offset, int length, CancellationToken ct)
    {
        if (length <= 0) return;

        if (_lineStart)
        {
            var prefix = Encoding.UTF8.GetBytes($"[{_opt.Clock():yyyy-MM-dd HH:mm:ss.fff}] ");
            await stream.WriteAsync(prefix, ct);
            _currentFileSize += prefix.Length;
            _lineStart = false;
        }

        var slice = data[offset..(offset + length)];
        if (_opt.TextEncoding == DataLogTextEncoding.Utf8)
        {
            await stream.WriteAsync(slice, ct);
            _currentFileSize += length;
        }
        else
        {
            var chars = new char[Encoding.GetEncoding("GBK").GetMaxCharCount(slice.Length)];
            var count = _gbkDecoder.GetChars(slice, chars, flush: false);
            var utf8 = Encoding.UTF8.GetBytes(chars, 0, count);
            await stream.WriteAsync(utf8, ct);
            _currentFileSize += utf8.Length;
        }
    }

    /// <summary>确保当前写入文件就绪：按天切换、超限分卷、打开失败重试。</summary>
    private void EnsureWriter(DateTime time)
    {
        var date = time.Year * 10000 + time.Month * 100 + time.Day;

        if (_writer is not null && _currentDate == date && _currentFileSize < _opt.MaxFileBytes)
            return;

        CloseWriter();

        try
        {
            Directory.CreateDirectory(_opt.Directory);
            if (!DiskHasSpace())
            {
                SetState(CaptureState.DiskShortage,
                    $"磁盘剩余空间不足（阈值 {_opt.MinFreeDiskBytes / 1048576} MB），日志已暂停");
                return;
            }
            if (State != CaptureState.Recording)
                SetState(CaptureState.Recording, null);

            if (_currentDate != date)
            {
                _currentDate = date;
                _currentVolume = 0;
            }

            var name = SanitizeName(_opt.MappingName);
            string Candidate(int volume) => Path.Combine(_opt.Directory,
                $"{name}_{date}{(volume > 1 ? $"_{volume:D3}" : "")}.log");
            // Resume the last existing volume on restart, without appending
            // to an already-full file or reusing an earlier numbered volume.
            if (_currentVolume == 0)
            {
                while (File.Exists(Candidate(_currentVolume + 1)))
                    _currentVolume++;
                if (_currentVolume == 0 || new FileInfo(Candidate(_currentVolume)).Length >= _opt.MaxFileBytes)
                    _currentVolume++;
            }
            else
            {
                _currentVolume++;
                while (File.Exists(Candidate(_currentVolume))) _currentVolume++;
            }
            _currentFile = Candidate(_currentVolume);

            _writer = new StreamWriter(
                new FileStream(_currentFile, FileMode.Append, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _currentFileSize = _writer.BaseStream.Length;

            LogX.Info($"串口日志：{_currentFile}");
        }
        catch (Exception ex)
        {
            _writer = null;
            _currentFile = null;
            SetState(CaptureState.Faulted, $"日志文件打开失败：{ex.Message}（每 30 秒自动重试）");
        }
    }

    private void CloseWriter()
    {
        // Dispose flushes the writer and releases its stream even if that flush fails.
        try { _writer?.Dispose(); }
        catch { }
        _writer = null;
    }

    private bool DiskHasSpace()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_opt.Directory));
            if (string.IsNullOrEmpty(root)) return true;
            var drive = new DriveInfo(root);
            if (!drive.IsReady) return true;
            return drive.AvailableFreeSpace >= _opt.MinFreeDiskBytes;
        }
        catch
        {
            return true;   // 无法判定时视为充足（写入失败另有降级）
        }
    }

    private void SetState(CaptureState state, string? reason)
    {
        if (_state == state && StateReason == reason) return;
        _state = state;
        StateReason = reason;
        LogX.Info($"串口日志状态：{state}{(reason is null ? "" : " — " + reason)}（丢弃块累计 {DroppedBlocks}）");
        try { StateChanged?.Invoke(); } catch { }
    }

    private void CleanupExpired()
    {
        try
        {
            if (_opt.RetentionDays <= 0) return;
            var prefix = SanitizeName(_opt.MappingName) + "_";
            var cutoff = _opt.Clock().AddDays(-_opt.RetentionDays);
            foreach (var f in Directory.EnumerateFiles(_opt.Directory, prefix + "*.log"))
            {
                // 仅删除本映射创建的日志（文件名前缀匹配），不触碰用户目录内的其他文件
                if (File.GetLastWriteTime(f) < cutoff)
                {
                    File.Delete(f);
                    LogX.Info($"串口日志清理：已删除过期文件 {Path.GetFileName(f)}");
                }
            }
        }
        catch (Exception ex)
        {
            LogX.Warn($"串口日志清理失败：{ex.Message}");
        }
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString().Trim();
    }

    private static string ToAscii(byte[] data)
    {
        var shown = Math.Min(data.Length, 64);
        var sb = new StringBuilder(shown);
        for (var i = 0; i < shown; i++)
        {
            var b = data[i];
            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '·');
        }
        return sb.ToString();
    }
}
