using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SerialBridge.App.Services;
using SerialBridge.App.Views;
using SerialBridge.Core.Bridge;
using SerialBridge.Core.Logging;
using SerialBridge.Core.Management;
using SerialBridge.Core.Models;
using SerialBridge.Core.Serial;

namespace SerialBridge.App.ViewModels;

/// <summary>一条映射的 UI 包装：订阅运行时事件并转换成可绑定属性。</summary>
public partial class MappingViewModel : ObservableObject
{
    public MappingRuntime Runtime { get; }

    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    public MappingViewModel(MappingRuntime runtime)
    {
        Runtime = runtime;
        runtime.StatusChanged += OnStatusChanged;
        Update();
    }

    public MappingConfig Config => Runtime.Config;

    // ---------- 可绑定状态 ----------

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _serialSummary = "";
    [ObservableProperty] private string _listenAddress = "";
    [ObservableProperty] private string _accessAddress = "";
    [ObservableProperty] private string _localAddress = "";
    [ObservableProperty] private string _captureStatusText = "";
    [ObservableProperty] private bool _hasCaptureStatus;
    [ObservableProperty] private string _stateText = "已停止";
    [ObservableProperty] private string _serialStateText = "";
    [ObservableProperty] private string _stateBrush = "GrayBrush";
    [ObservableProperty] private string _stateIcon = "\uE946";
    [ObservableProperty] private int _clientCount;
    [ObservableProperty] private string _clientsButtonText = "客户端 0";
    [ObservableProperty] private long _rxBytes;
    [ObservableProperty] private long _txBytes;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _hasFault;
    [ObservableProperty] private string _faultMessage = "";
    [ObservableProperty] private string _modeText = "单客户端";

    private void OnStatusChanged(MappingRuntime _) => _ui?.Post(_ => Update(), null);

    public void Update()
    {
        var rt = Runtime;
        var status = rt.GetStatus();

        Name = Config.Name;
        SerialSummary = Config.SerialSummary;
        ListenAddress = $"{Config.ListenIp}:{Config.ListenPort}";
        AccessAddress = rt.AccessAddress;
        LocalAddress = $"127.0.0.1:{Config.ListenPort}";
        UpdateCaptureStatus();
        ClientCount = status.ClientCount;
        ClientsButtonText = $"👥 客户端 {status.ClientCount}";
        RxBytes = status.SerialRxBytes;
        TxBytes = status.SerialTxBytes;
        ModeText = Config.ClientMode == ClientMode.Single
            ? "单客户端"
            : $"多客户端 ≤{Config.MaxClients}";

        IsRunning = status.State is MappingState.Running or MappingState.Starting;

        switch (status.State, status.SerialState)
        {
            case (MappingState.Faulted, _):
                SetBadge("启动失败", "DangerBrush", "\uEA39", fault: true);
                break;
            case (MappingState.Stopped, _):
                SetBadge("已停止", "GrayBrush", "\uE946", fault: false);
                break;
            case (MappingState.Starting, _):
                SetBadge("启动中…", "CautionBrush", "\uF16A", fault: false);
                break;
            case (_, SerialState.Open):
                SetBadge("运行中", "SuccessBrush", "\uE73E", fault: false);
                break;
            case (_, SerialState.Opening):
                SetBadge("串口打开中…", "CautionBrush", "\uF16A", fault: false);
                break;
            case (_, SerialState.ReconnectWaiting):
                SetBadge("串口重连中", "CautionBrush", "\uF16A", fault: false);
                break;
            case (_, SerialState.Faulted):
                SetBadge("串口异常", "DangerBrush", "\uEA39", fault: true);
                break;
            default:
                SetBadge("运行中", "CautionBrush", "\uF16A", fault: false);
                break;
        }

        // TCP 在听但串口不可用时，给出串口侧的补充说明
        FaultMessage = status.State switch
        {
            MappingState.Faulted => rt.FaultMessage ?? "启动失败",
            _ => status.SerialState switch
            {
                SerialState.Faulted => status.SerialMessage ?? "串口异常",
                _ => "",
            },
        };

        SerialStateText = status.SerialState switch
        {
            SerialState.Open => "串口已连接",
            SerialState.Opening => "串口打开中",
            SerialState.ReconnectWaiting => status.SerialMessage ?? "重连中",
            SerialState.Faulted => status.SerialMessage ?? "串口异常",
            _ => "串口未连接",
        };
    }

    private void UpdateCaptureStatus()
    {
        var cap = Runtime.Capture;
        HasCaptureStatus = cap is not null;
        if (cap is null)
        {
            CaptureStatusText = "";
            return;
        }
        CaptureStatusText = cap.State switch
        {
            Core.Capture.CaptureState.Recording => $"● 记录中 {System.IO.Path.GetFileName(cap.CurrentFile ?? "")}",
            Core.Capture.CaptureState.DiskShortage => "⚠ 日志暂停：磁盘空间不足",
            _ => $"⚠ 日志暂停：{cap.StateReason}",
        };
    }

    private void SetBadge(string text, string brush, string icon, bool fault)
    {
        StateText = text;
        StateBrush = brush;
        StateIcon = icon;
        HasFault = fault;
    }

    // ---------- 操作 ----------

    [RelayCommand]
    private async Task ToggleRunAsync()
    {
        if (Runtime.IsRunning)
        {
            await Runtime.StopAsync();
        }
        else
        {
            var error = await AppServices.Manager.StartMappingAsync(Runtime);
            if (error is not null)
            {
                FaultMessage = error;
                LogX.Warn($"映射「{Config.Name}」启动失败：{error}");
            }
        }
        Update();
        AppServices.SaveSettingsSafe();
    }

    [RelayCommand]
    private void OpenMonitor() => MonitorWindow.OpenFor(Runtime);

    [RelayCommand]
    private void OpenClients() => ClientsWindow.OpenFor(Runtime);

    [RelayCommand]
    private void OpenTerminal() => TerminalWindow.OpenFor(Runtime);

    [RelayCommand]
    private void OpenCaptureFolder()
    {
        var dir = Runtime.Capture?.CurrentFile is { } f ? Path.GetDirectoryName(f) : Config.DataLog.Directory;
        if (string.IsNullOrWhiteSpace(dir) || !System.IO.Directory.Exists(dir))
        {
            MessageBox.Show("日志目录不存在或尚未产生日志。", "串口日志",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
    }

    [RelayCommand]
    private void Edit() => MainViewModel.RequestEdit?.Invoke(this);

    [RelayCommand]
    private async Task DeleteAsync()
    {
        var result = MessageBox.Show(
            $"确定删除映射「{Config.Name}」吗？\n（运行中的串口与监听端口将立即释放）",
            "删除映射", MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return;
        await AppServices.Manager.RemoveMappingAsync(Runtime);
        AppServices.SaveSettingsSafe();
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F2} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes} B",
    };
}
