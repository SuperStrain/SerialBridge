using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SerialBridge.Core.Bridge;
using SerialBridge.Core.Models;

namespace SerialBridge.App.ViewModels;

/// <summary>串口终端中的一条输出记录（复用监控行结构，仅显示设备输出）。</summary>
public partial class TerminalLine : ObservableObject
{
    public DateTime Time { get; init; }

    [ObservableProperty] private string _hexText = "";
    [ObservableProperty] private string _asciiText = "";

    [ObservableProperty] private bool _showHex;
    partial void OnShowHexChanged(bool value) => OnPropertyChanged(nameof(ContentText));

    public string TimeText => Time.ToLocalTime().ToString("HH:mm:ss.fff");
    public string ContentText => ShowHex ? HexText : AsciiText;
}

/// <summary>
/// 串口终端 ViewModel：显示设备输出（SerialToNet），键盘输入经
/// MappingRuntime.WriteLocal 进入共享写队列（与 TCP 客户端 FIFO 串行）。
/// 显示批量刷新 + 限量缓存，保证高频输出下 UI 不卡顿。
/// </summary>
public partial class TerminalViewModel : ObservableObject
{
    private const int MaxLines = 3000;
    private const int MaxCaptureBytes = 4096;
    private const int MaxDisplayBytes = 512;
    private const int FlushIntervalMs = 120;

    private readonly MappingRuntime _runtime;
    private readonly DispatcherTimer _flushTimer;
    private readonly Queue<PendingItem> _pending = new();
    private readonly object _pendingGate = new();
    private readonly List<string> _history = new();
    private int _historyIndex = -1;

    [ObservableProperty] private string _title = "串口终端";
    [ObservableProperty] private bool _showHex;
    [ObservableProperty] private string _inputText = "";
    [ObservableProperty] private bool _hexMode;
    [ObservableProperty] private bool _canSend;
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private int _lineCount;
    [ObservableProperty] private long _txBytes;
    [ObservableProperty] private long _rxBytes;
    [ObservableProperty] private string _rxText = "0 B";
    [ObservableProperty] private string _txText = "0 B";

    public ObservableCollection<TerminalLine> Lines { get; } = new();

    public TerminalViewModel(MappingRuntime runtime)
    {
        _runtime = runtime;
        Title = $"串口终端 — {runtime.Config.Name}";
        runtime.DataTransferred += OnDataTransferred;
        runtime.StatusChanged += OnStatusChanged;

        _flushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(FlushIntervalMs),
        };
        _flushTimer.Tick += (_, _) => FlushPending();
        _flushTimer.Start();
        OnStatusChanged(null);
    }

    private void OnDataTransferred(MonitorEntry entry)
    {
        if (entry.Direction != DataDirection.SerialToNet) return;   // 终端只显示设备输出
        lock (_pendingGate)
            _pending.Enqueue(new PendingItem(DateTime.UtcNow, (byte[])entry.Data.Clone()));
    }

    private void OnStatusChanged(object? _)
    {
        var status = _runtime.GetStatus();
        CanSend = _runtime.IsRunning && status.SerialState == SerialState.Open;
        TxBytes = status.SerialTxBytes;
        RxBytes = status.SerialRxBytes;
        TxText = MappingViewModel.FormatBytes(TxBytes);
        RxText = MappingViewModel.FormatBytes(RxBytes);
        StateText = status.SerialState switch
        {
            SerialState.Open => "设备已连接",
            SerialState.Faulted => "设备串口异常",
            SerialState.ReconnectWaiting => "设备串口重连中",
            SerialState.Opening => "设备串口打开中",
            _ => "设备未连接",
        };
        if (!CanSend)
            StateText += "（映射需处于运行中且串口已连接才能发送）";
    }

    // ---------- 批量刷新 ----------

    private void FlushPending()
    {
        PendingItem[] items;
        lock (_pendingGate)
        {
            items = _pending.ToArray();
            _pending.Clear();
        }
        if (items.Length == 0) return;

        foreach (var item in items)
        {
            var capture = item.Data.Length > MaxCaptureBytes ? item.Data[..MaxCaptureBytes] : item.Data;
            var truncated = item.Data.Length - capture.Length;
            Lines.Add(new TerminalLine
            {
                Time = item.Time,
                HexText = ToHex(capture) + (truncated > 0 ? $"  …(+{truncated})" : ""),
                AsciiText = ToAscii(capture) + (truncated > 0 ? $"  …(+{truncated})" : ""),
                ShowHex = ShowHex,
            });
        }
        if (Lines.Count > MaxLines)
            for (var i = 0; i < Lines.Count - MaxLines; i++)
                Lines.RemoveAt(0);
        LineCount = Lines.Count;
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

    // ---------- 发送 ----------

    [RelayCommand]
    private void Send()
    {
        var text = InputText;
        if (string.IsNullOrEmpty(text)) return;

        byte[] payload;
        if (HexMode)
        {
            var clean = text.Replace(" ", "").Replace("\t", "");
            if (clean.Length == 0 || clean.Length % 2 != 0)
            {
                System.Windows.MessageBox.Show("HEX 内容必须为偶数个十六进制字符（如 41 54 0D）。", "串口终端",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }
            try
            {
                payload = Convert.FromHexString(clean);
            }
            catch (FormatException)
            {
                System.Windows.MessageBox.Show("包含非法的十六进制字符。", "串口终端",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }
        }
        else
        {
            // 终端回车 = 命令行结束（CR）
            payload = Encoding.UTF8.GetBytes(text + "\r");
        }

        if (_runtime.WriteLocal(payload))
        {
            if (!HexMode && !string.IsNullOrWhiteSpace(text))
            {
                _history.Remove(text);
                _history.Add(text);
                if (_history.Count > 100) _history.RemoveAt(0);
            }
            InputText = "";
            _historyIndex = -1;
        }
    }

    public void OnHistoryKey(bool up)
    {
        if (_history.Count == 0) return;
        if (up)
        {
            _historyIndex = _historyIndex < 0 ? _history.Count - 1 : Math.Max(0, _historyIndex - 1);
            InputText = _history[_historyIndex];
        }
        else
        {
            _historyIndex = Math.Min(_history.Count - 1, _historyIndex + 1);
            InputText = _historyIndex >= _history.Count ? "" : _history[_historyIndex];
        }
    }

    [RelayCommand]
    private void SendCtrlC() => _runtime.WriteLocal(new byte[] { 0x03 });

    [RelayCommand]
    private void SendCtrlZ() => _runtime.WriteLocal(new byte[] { 0x1A });

    [RelayCommand]
    private void Clear()
    {
        Lines.Clear();
        LineCount = 0;
    }

    public void Detach()
    {
        _flushTimer.Stop();
        _runtime.DataTransferred -= OnDataTransferred;
        _runtime.StatusChanged -= OnStatusChanged;
    }

    private readonly record struct PendingItem(DateTime Time, byte[] Data);
}
