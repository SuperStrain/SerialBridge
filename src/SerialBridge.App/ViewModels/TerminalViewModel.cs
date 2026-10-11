using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SerialBridge.Core.Bridge;
using SerialBridge.Core.Models;

namespace SerialBridge.App.ViewModels;

/// <summary>串口终端 ViewModel：状态与统计（渲染与键盘在 WebView2/xterm.js 层）。</summary>
public partial class TerminalViewModel : ObservableObject
{
    private readonly MappingRuntime _runtime;

    [ObservableProperty] private string _title = "串口终端";
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private long _txBytes;
    [ObservableProperty] private long _rxBytes;
    [ObservableProperty] private string _rxText = "0 B";
    [ObservableProperty] private string _txText = "0 B";

    public TerminalViewModel(MappingRuntime runtime)
    {
        _runtime = runtime;
        Title = $"串口终端 — {runtime.Config.Name}";
        runtime.StatusChanged += OnStatusChanged;
        OnStatusChanged(null);
    }

    private void OnStatusChanged(object? _)
    {
        var status = _runtime.GetStatus();
        TxBytes = status.SerialTxBytes;
        RxBytes = status.SerialRxBytes;
        TxText = MappingViewModel.FormatBytes(TxBytes);
        RxText = MappingViewModel.FormatBytes(RxBytes);
        StateText = _runtime.IsRunning
            ? status.SerialState switch
            {
                SerialState.Open => "设备已连接 — 逐键透传中",
                SerialState.Faulted => "设备串口异常",
                SerialState.ReconnectWaiting => "设备串口重连中，稍候恢复",
                SerialState.Opening => "设备串口打开中…",
                _ => "设备未连接",
            }
            : "映射未运行 — 请在主界面启动映射后使用终端";
    }

    /// <summary>按键透传流量（WebView2 层调用）。</summary>
    public void NoteSent(int bytesCount)
    {
        // 统计已在 MappingRuntime.WriteLocal 计入 SerialTxBytes，此处刷新显示
        var status = _runtime.GetStatus();
        TxText = MappingViewModel.FormatBytes(status.SerialTxBytes);
    }

    public void NoteReceived(int bytesCount)
    {
        var status = _runtime.GetStatus();
        RxText = MappingViewModel.FormatBytes(status.SerialRxBytes);
    }

    public void Detach() => _runtime.StatusChanged -= OnStatusChanged;
}
