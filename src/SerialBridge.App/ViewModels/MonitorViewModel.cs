using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SerialBridge.Core.Bridge;
using SerialBridge.Core.Models;

namespace SerialBridge.App.ViewModels;

/// <summary>监控窗口中的一条显示记录。</summary>
public partial class MonitorLine : ObservableObject
{
    public DateTime Time { get; init; }
    public DataDirection Direction { get; init; }

    [ObservableProperty] private string _hexText = "";
    [ObservableProperty] private string _asciiText = "";
    [ObservableProperty] private bool _isVisible;

    public string TimeText => Time.ToLocalTime().ToString("HH:mm:ss.fff");
    public string DirectionText => Direction == DataDirection.SerialToNet ? "串口 → 网络" : "网络 → 串口";
    public bool IsOutbound => Direction == DataDirection.SerialToNet;
    public string ContentText => ShowHex ? HexText : AsciiText;

    /// <summary>当前显示模式（HEX/ASCII）。</summary>
    [ObservableProperty] private bool _showHex;

    partial void OnShowHexChanged(bool value) => OnPropertyChanged(nameof(ContentText));
}

/// <summary>数据监控窗口 ViewModel：限量缓存 + 批量刷新，保证高频数据下 UI 不卡顿。</summary>
public partial class MonitorViewModel : ObservableObject
{
    private const int MaxLines = 3000;          // UI 缓存上限
    private const int MaxDisplayBytes = 512;    // HEX/ASCII 显示的最大字节数
    private const int FlushIntervalMs = 150;
    private const int MaxPendingEntries = 3000;
    private const int MaxPendingBytes = 8 * 1024 * 1024;    // 批量刷新周期

    private readonly MappingRuntime _runtime;
    private readonly DispatcherTimer _flushTimer;
    private readonly ConcurrentQueueProducer _pending = new();

    [ObservableProperty] private string _title = "数据监控";
    [ObservableProperty] private bool _showHex = true;

    partial void OnShowHexChanged(bool value)
    {
        foreach (var line in Lines)
            line.ShowHex = value;
    }
    [ObservableProperty] private bool _autoScroll = true;
    [ObservableProperty] private bool _paused;
    [ObservableProperty] private long _rxBytes;
    [ObservableProperty] private long _txBytes;
    [ObservableProperty] private int _clientCount;
    [ObservableProperty] private string _rxText = "0 B";
    [ObservableProperty] private string _txText = "0 B";
    [ObservableProperty] private int _lineCount;
    [ObservableProperty] private long _omittedEntries;
    [ObservableProperty] private string _serialStateText = "";

    public ObservableCollection<MonitorLine> Lines { get; } = new();

    public MonitorViewModel(MappingRuntime runtime)
    {
        _runtime = runtime;
        Title = $"数据监控 — {runtime.Config.Name}";
        runtime.DataTransferred += OnDataTransferred;
        runtime.StatusChanged += OnStatusChanged;

        _flushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(FlushIntervalMs),
        };
        _flushTimer.Tick += (_, _) => FlushPending();
        _flushTimer.Start();
        OnStatusChanged(_runtime);
    }

    private void OnDataTransferred(MonitorEntry entry)
    {
        // 暂停显示时不丢统计，仅跳过记录
        if (Paused) return;
        var data = entry.Data.Length > MaxDisplayBytes ? entry.Data[..MaxDisplayBytes] : entry.Data;
        _pending.Enqueue(new PendingItem(entry.TimeUtc, entry.Direction, (byte[])data.Clone(), entry.Data.Length));
    }

    private void OnStatusChanged(MappingRuntime _)
    {
        var status = _runtime.GetStatus();
        RxBytes = status.SerialRxBytes;
        TxBytes = status.SerialTxBytes;
        ClientCount = status.ClientCount;
        RxText = MappingViewModel.FormatBytes(RxBytes);
        TxText = MappingViewModel.FormatBytes(TxBytes);
        SerialStateText = status.SerialState switch
        {
            SerialState.Open => "串口已连接",
            SerialState.Faulted => "串口异常",
            SerialState.ReconnectWaiting => "串口重连中",
            SerialState.Opening => "串口打开中",
            _ => "串口未连接",
        };
    }

    // ---------- 批量刷新 ----------

    private void FlushPending()
    {
        OmittedEntries = _pending.Dropped;
        if (Paused) { _pending.Clear(); return; }
        var items = _pending.Drain(400);
        if (items.Count == 0) return;

        foreach (var item in items) Lines.Add(BuildLine(item));
        if (Lines.Count > MaxLines)
            for (var i = 0; i < Lines.Count - MaxLines; i++)
                Lines.RemoveAt(0);
        LineCount = Lines.Count;
    }

    private MonitorLine BuildLine(PendingItem item)
    {
        var capture = item.Data;
        var truncated = item.OriginalLength - capture.Length;

        var line = new MonitorLine
        {
            Time = item.Time,
            Direction = item.Direction,
            HexText = ToHex(capture) + (truncated > 0 ? $"  …(+{truncated} 字节)" : ""),
            AsciiText = ToAscii(capture) + (truncated > 0 ? $"  …(+{truncated} 字节)" : ""),
            ShowHex = ShowHex,
            IsVisible = true,
        };
        return line;
    }

    private static string ToHex(byte[] data)
    {
        var shown = Math.Min(data.Length, MaxDisplayBytes);
        var sb = new StringBuilder(shown * 3);
        for (var i = 0; i < shown; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[i].ToString("X2"));
        }
        return sb.ToString();
    }

    private static string ToAscii(byte[] data)
    {
        var shown = Math.Min(data.Length, MaxDisplayBytes);
        var sb = new StringBuilder(shown);
        for (var i = 0; i < shown; i++)
        {
            var b = data[i];
            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '·');
        }
        return sb.ToString();
    }

    // ---------- 命令 ----------

    [RelayCommand]
    private void TogglePause()
    {
        Paused = !Paused;
        if (Paused) _pending.Clear();
    }

    [RelayCommand]
    private void Clear()
    {
        Lines.Clear();
        LineCount = 0;
    }

    [RelayCommand]
    private void Export()
    {
        if (Lines.Count == 0)
        {
            System.Windows.MessageBox.Show("当前没有可导出的数据。", "导出",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出通信日志",
            Filter = "文本文件 (*.txt)|*.txt|CSV 文件 (*.csv)|*.csv",
            FileName = $"SerialBridge_{_runtime.Config.Name}_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# SerialBridge 通信日志 — 映射「{_runtime.Config.Name}」");
            sb.AppendLine($"# 导出当前视图（非完整串口捕获）：{DateTime.Now:yyyy-MM-dd HH:mm:ss}  共 {Lines.Count} 条记录");
            sb.AppendLine($"# 每条记录最多显示 {MaxDisplayBytes} 字节；累计省略 {OmittedEntries} 条监控记录。");
            sb.AppendLine("# 方向：OUT = 串口→网络（设备上传），IN = 网络→串口（客户端下发）");
            foreach (var line in Lines)
            {
                var dir = line.IsOutbound ? "OUT" : "IN ";
                sb.AppendLine($"{line.Time.ToLocalTime():HH:mm:ss.fff}  {dir}  HEX  {line.HexText}");
                sb.AppendLine($"{line.Time.ToLocalTime():HH:mm:ss.fff}  {dir}  TXT  {line.AsciiText}");
            }
            File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
            System.Windows.MessageBox.Show($"已导出 {Lines.Count} 条记录到：\n{dialog.FileName}", "导出成功",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"导出失败：{ex.Message}", "导出",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    public void Detach()
    {
        _flushTimer.Stop();
        _runtime.DataTransferred -= OnDataTransferred;
        _runtime.StatusChanged -= OnStatusChanged;
    }

    /// <summary>线程安全的待显示队列。</summary>
    private sealed class ConcurrentQueueProducer
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<PendingItem> _queue = new();
        private readonly object _sync = new();
        private long _pendingBytes;
        private long _dropped;

        public long Dropped => Interlocked.Read(ref _dropped);

        public void Enqueue(PendingItem item)
        {
            lock (_sync)
            {
                while (_queue.Count >= MaxPendingEntries ||
                       (_pendingBytes + item.Data.Length > MaxPendingBytes && !_queue.IsEmpty))
                {
                    if (!_queue.TryDequeue(out var removed)) break;
                    _pendingBytes -= removed.Data.Length;
                    Interlocked.Increment(ref _dropped);
                }
                _queue.Enqueue(item);
                _pendingBytes += item.Data.Length;
            }
        }

        public List<PendingItem> Drain(int maximum)
        {
            var list = new List<PendingItem>(maximum);
            lock (_sync)
            {
                while (list.Count < maximum && _queue.TryDequeue(out var item))
                {
                    _pendingBytes -= item.Data.Length;
                    list.Add(item);
                }
            }
            return list;
        }

        public void Clear()
        {
            lock (_sync)
            {
                while (_queue.TryDequeue(out var item)) _pendingBytes -= item.Data.Length;
            }
        }
    }

    private readonly record struct PendingItem(DateTime Time, DataDirection Direction, byte[] Data, int OriginalLength);
}
