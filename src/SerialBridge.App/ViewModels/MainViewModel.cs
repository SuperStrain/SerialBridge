using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SerialBridge.App.Services;
using SerialBridge.Core.Logging;
using SerialBridge.Core.Management;
using SerialBridge.Core.Config;
using SerialBridge.Core.Models;
using SerialBridge.Core.Net;

namespace SerialBridge.App.ViewModels;

/// <summary>主窗口（仪表盘）ViewModel。</summary>
public partial class MainViewModel : ObservableObject
{
    /// <summary>由 MainWindow 注册：打开映射编辑对话框。</summary>
    public static Action<MappingViewModel>? RequestEdit;

    public ObservableCollection<MappingViewModel> Mappings { get; } = new();

    [ObservableProperty] private int _runningCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _totalClients;
    [ObservableProperty] private long _totalRx;
    [ObservableProperty] private long _totalTx;
    [ObservableProperty] private string _totalRxText = "0 B";
    [ObservableProperty] private string _totalTxText = "0 B";
    [ObservableProperty] private string _statusSummary = "未运行任何映射";
    [ObservableProperty] private string _statusBadgeText = "";
    [ObservableProperty] private string _localIpSummary = "";
    [ObservableProperty] private string _localIpsText = "";
    [ObservableProperty] private int _localIpCount;
    [ObservableProperty] private IReadOnlyList<string> _localIpList = Array.Empty<string>();
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private bool _hasMappings;
    [ObservableProperty] private bool _firewallWarningVisible;
    [ObservableProperty] private bool _updateBadgeVisible;
    [ObservableProperty] private string _updateBadgeVersion = "";

    public UpdateViewModel? Update { get; private set; }
    [ObservableProperty] private string _refreshedAtText = "";
    [ObservableProperty] private string _dataDirText = Core.Logging.LogService.AppDataDir;
    [ObservableProperty] private string _appVersion = Services.AppServices.AppVersion;

    /// <summary>批量按钮文案：有勾选时作用于所选映射，避免"全部"字样产生歧义。</summary>
    public string StartAllText => SelectedCount > 0 ? $"启动所选 ({SelectedCount})" : "全部启动";
    public string StopAllText => SelectedCount > 0 ? $"停止所选 ({SelectedCount})" : "全部停止";

    /// <summary>表头全选框：全选时勾起，随行勾选联动刷新。</summary>
    public bool AllSelected
    {
        get => Mappings.Count > 0 && Mappings.All(m => m.IsSelected);
        set
        {
            foreach (var m in Mappings) m.IsSelected = value;
            OnPropertyChanged();
        }
    }

    partial void OnSelectedCountChanged(int value)
    {
        OnPropertyChanged(nameof(StartAllText));
        OnPropertyChanged(nameof(StopAllText));
        OnPropertyChanged(nameof(AllSelected));
    }

    private void OnMappingSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MappingViewModel.IsSelected))
            SelectedCount = Mappings.Count(m => m.IsSelected);
    }

    private readonly DispatcherTimerEx _timer;
    private DispatcherTimerEx _updateTimer;

    public MainViewModel()
    {
        RequestEdit += OnRequestEdit;
        AppServices.Manager.MappingsChanged += RebuildMappings;
        InitializeUpdate();

        // 每秒刷新统计与状态摘要
        _timer = new DispatcherTimerEx(TimeSpan.FromSeconds(1), RefreshStats);
        RefreshLocalIps();
        CheckFirewall();
        RebuildMappings();
        RefreshStats();
    }

    // ---------- 自动升级 ----------

    private void InitializeUpdate()
    {
        var apiOverride = Environment.GetEnvironmentVariable("SERIALBRIDGE_UPDATE_API");
        var fallbackOverride = Environment.GetEnvironmentVariable("SERIALBRIDGE_UPDATE_FALLBACK");
        var updater = new UpdateService(new AppUpdateEnvironment(),
            apiUrlOverride: string.IsNullOrWhiteSpace(apiOverride) ? null : apiOverride,
            fallbackUrlOverride: string.IsNullOrWhiteSpace(fallbackOverride) ? null : fallbackOverride);
        Update = new UpdateViewModel(updater, SkippedVersionStore.Load());

        // 属性转发（NEW 徽章绑定在 MainViewModel 上）
        Update.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(Update.UpdateBadgeVisible): UpdateBadgeVisible = Update.UpdateBadgeVisible; break;
                case nameof(Update.UpdateBadgeVersion): UpdateBadgeVersion = Update.UpdateBadgeVersion; break;
            }
        };

        // 启动 30 秒后首次静默检查；之后每 30 分钟复查一次（托盘常驻运行也能及时发现新版本）。
        // 每次 tick 实时读取开关：用户中途关闭自动检查后立即生效。
        _updateTimer = new DispatcherTimerEx(TimeSpan.FromMinutes(30), OnUpdateTimerTick);
        if (AppServices.Settings.General.AutoCheckUpdates)
        {
            _ = System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
                LogX.Info("自动检查更新：开始");
                var v = await Update.CheckSilentlyAsync();
                LogX.Info($"自动检查更新：结果 {(v is null ? "无更新/失败/已跳过" : "v" + v)}");
            });
        }
    }

    private async void OnUpdateTimerTick()
    {
        try
        {
            if (!AppServices.Settings.General.AutoCheckUpdates) return;
            LogX.Info("定时检查更新：开始");
            var v = await Update!.CheckSilentlyAsync();
            LogX.Info($"定时检查更新：结果 {(v is null ? "无更新/失败/已跳过" : "v" + v)}");
        }
        catch (Exception ex)
        {
            // 定时任务绝不干扰主流程
            LogX.Warn($"定时检查更新异常：{ex.Message}");
        }
    }

    private void RefreshLocalIps()
    {
        var ips = NetworkInfo.GetLocalIPv4Addresses().Select(ip => ip.ToString()).ToList();
        LocalIpList = ips;
        LocalIpCount = ips.Count;
        LocalIpsText = ips.Count > 0 ? $"本机 IPv4：{ips.Count} 个地址" : "本机 IPv4：未检测到";
        LocalIpSummary = ips.Count > 0 ? string.Join("  ", ips) : "未检测到局域网地址";
    }

    // ---------- 映射列表维护 ----------

    private void RebuildMappings()
    {
        var runtimes = AppServices.Manager.Mappings.ToList();
        // 同步集合：保留已有的 VM，移除消失的，添加新增的
        for (var i = Mappings.Count - 1; i >= 0; i--)
        {
            var idx = runtimes.FindIndex(r => ReferenceEquals(r, Mappings[i].Runtime));
            if (idx < 0)
            {
                Mappings[i].PropertyChanged -= OnMappingSelectionChanged;
                Mappings.RemoveAt(i);
            }
        }
        foreach (var rt in runtimes)
            if (!Mappings.Any(vm => ReferenceEquals(vm.Runtime, rt)))
            {
                var vm = new MappingViewModel(rt);
                vm.PropertyChanged += OnMappingSelectionChanged;
                Mappings.Add(vm);
            }
        HasMappings = Mappings.Count > 0;
        SelectedCount = Mappings.Count(m => m.IsSelected);
    }

    // ---------- 顶部统计 ----------

    private void RefreshStats()
    {
        var mappings = AppServices.Manager.Mappings;
        RunningCount = mappings.Count(m => m.IsRunning);
        TotalCount = mappings.Count;
        TotalClients = mappings.Sum(m => m.ClientCount);
        TotalRx = mappings.Sum(m => m.SerialRxBytes);
        TotalTx = mappings.Sum(m => m.SerialTxBytes);
        TotalRxText = MappingViewModel.FormatBytes(TotalRx);
        TotalTxText = MappingViewModel.FormatBytes(TotalTx);
        StatusSummary = RunningCount == 0
            ? (TotalCount == 0 ? "尚未创建映射" : "所有映射已停止")
            : $"{RunningCount}/{TotalCount} 条映射运行中";
        StatusBadgeText = RunningCount > 0 ? "服务运行中" : StatusSummary;
        RefreshedAtText = $"统计时间：{DateTime.Now:HH:mm:ss}";
    }

    // ---------- 命令 ----------

    [RelayCommand]
    private void AddMapping() => ShowEditDialog(null);

    private void OnRequestEdit(MappingViewModel vm) => ShowEditDialog(vm);

    private void ShowEditDialog(MappingViewModel? existing)
    {
        var owner = System.Windows.Application.Current.Windows.OfType<Views.MainWindow>().FirstOrDefault();
        var dialog = new Views.MappingEditWindow(owner, existing);
        dialog.ShowDialog();
    }

    [RelayCommand]
    private async Task StartAllAsync()
    {
        // 勾选了部分映射时只作用于所选（按钮文案会同步变为"启动所选"）
        if (SelectedCount > 0 && SelectedCount < Mappings.Count)
        {
            foreach (var m in Mappings.Where(m => m.IsSelected))
                await AppServices.Manager.StartMappingAsync(m.Runtime);
        }
        else
        {
            await AppServices.Manager.StartAllAsync();
        }
        AppServices.SaveSettingsSafe();
    }

    [RelayCommand]
    private async Task StopAllAsync()
    {
        if (SelectedCount > 0 && SelectedCount < Mappings.Count)
        {
            foreach (var m in Mappings.Where(m => m.IsSelected))
                await m.Runtime.StopAsync();
        }
        else
        {
            await AppServices.Manager.StopAllAsync();
        }
        AppServices.SaveSettingsSafe();
    }

    [RelayCommand]
    private void RefreshPorts()
    {
        PortNames.ReplaceAll(SerialPortInfo.GetPortNames());
        RefreshLocalIps();
        RefreshedAtText = $"串口列表已刷新 {DateTime.Now:HH:mm:ss}";
    }

    [RelayCommand]
    private void AddFirewallRule()
    {
        if (FirewallHelper.TryAddRule(out var message))
        {
            FirewallWarningVisible = false;
            System.Windows.MessageBox.Show(message, "防火墙", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }
        else
        {
            System.Windows.MessageBox.Show(
                message + "\n\n也可以手动放行：控制面板 → Windows Defender 防火墙 → 允许应用通过防火墙。",
                "防火墙", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private void CheckFirewall()
    {
        // 已添加过规则或程序目录在系统目录（不可能）则不提示
        FirewallWarningVisible = !FirewallHelper.IsRuleInstalled();
    }

    public void Shutdown()
    {
        RequestEdit -= OnRequestEdit;
        _timer.Dispose();
        _updateTimer.Dispose();
    }
}

/// <summary>映射卡片可用的串口列表（编辑对话框也会读取系统实时列表）。</summary>
public static class PortNames
{
    public static ObservableCollection<string> Items { get; } = new();
    public static void ReplaceAll(IEnumerable<string> names)
    {
        Items.Clear();
        foreach (var n in names) Items.Add(n);
    }
}

/// <summary>轻量 DispatcherTimer 封装（便于释放与测试）。</summary>
public sealed class DispatcherTimerEx : IDisposable
{
    private readonly System.Windows.Threading.DispatcherTimer _timer;

    public DispatcherTimerEx(TimeSpan interval, Action tick)
    {
        _timer = new System.Windows.Threading.DispatcherTimer(interval,
            System.Windows.Threading.DispatcherPriority.Background, (_, _) => tick(),
            System.Windows.Threading.Dispatcher.CurrentDispatcher);
        _timer.Start();
    }

    public void Dispose() => _timer.Stop();
}
