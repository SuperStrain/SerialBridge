using System.Collections.ObjectModel;
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
    [ObservableProperty] private string _localIpSummary = "";
    [ObservableProperty] private bool _hasMappings;
    [ObservableProperty] private bool _firewallWarningVisible;
    [ObservableProperty] private bool _updateBannerVisible;
    [ObservableProperty] private string _updateBannerTitle = "";
    [ObservableProperty] private string _updateBannerMessage = "";

    public UpdateViewModel? Update { get; private set; }
    [ObservableProperty] private string _refreshedAtText = "";
    [ObservableProperty] private string _dataDirText = Core.Logging.LogService.AppDataDir;
    [ObservableProperty] private string _appVersion = Services.AppServices.AppVersion;

    private readonly DispatcherTimerEx _timer;

    public MainViewModel()
    {
        RequestEdit += OnRequestEdit;
        AppServices.Manager.MappingsChanged += RebuildMappings;
        InitializeUpdate();

        // 每秒刷新统计与状态摘要
        _timer = new DispatcherTimerEx(TimeSpan.FromSeconds(1), RefreshStats);
        LocalIpSummary = BuildLocalIpSummary();
        CheckFirewall();
        RebuildMappings();
        RefreshStats();
    }

    // ---------- 自动升级 ----------

    private void InitializeUpdate()
    {
        var updater = new UpdateService(new AppUpdateEnvironment());
        Update = new UpdateViewModel(updater, SkippedVersionStore.Load());

        // 属性转发（横幅绑定在 MainViewModel 上）
        Update.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(Update.UpdateBannerVisible): UpdateBannerVisible = Update.UpdateBannerVisible; break;
                case nameof(Update.UpdateBannerTitle): UpdateBannerTitle = Update.UpdateBannerTitle; break;
                case nameof(Update.UpdateBannerMessage): UpdateBannerMessage = Update.UpdateBannerMessage; break;
            }
        };

        // 命令委托
        _updateNowCommand = new AsyncRelayCommand(() => Update.UpdateNowAsync());
        _skipVersionCommand = new RelayCommand(() => Update.SkipVersion());

        // 启动后 30 秒自动检查（用户关闭开关则不查）；之后每 6 小时一次
        if (AppServices.Settings.General.AutoCheckUpdates)
        {
            _ = System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
                var v = await Update.CheckSilentlyAsync();
                if (v is not null)
                {
                    UpdateBannerVisible = true;
                    LogX.Info($"发现新版本 v{v}");
                }
            });
        }
    }

    private AsyncRelayCommand? _updateNowCommand;
    public IAsyncRelayCommand UpdateNowCommand => _updateNowCommand ??= new AsyncRelayCommand(
        () => Update?.UpdateNowAsync() ?? Task.CompletedTask);
    private RelayCommand? _skipVersionCommand;
    public IRelayCommand SkipVersionCommand => _skipVersionCommand ??= new RelayCommand(
        () => Update?.SkipVersion());

    private static string BuildLocalIpSummary()
    {
        var ips = NetworkInfo.GetLocalIPv4Addresses();
        return ips.Count > 0 ? string.Join("  ", ips.Select(ip => ip.ToString())) : "未检测到局域网地址";
    }

    // ---------- 映射列表维护 ----------

    private void RebuildMappings()
    {
        var runtimes = AppServices.Manager.Mappings.ToList();
        // 同步集合：保留已有的 VM，移除消失的，添加新增的
        for (var i = Mappings.Count - 1; i >= 0; i--)
        {
            var idx = runtimes.FindIndex(r => ReferenceEquals(r, Mappings[i].Runtime));
            if (idx < 0) Mappings.RemoveAt(i);
        }
        foreach (var rt in runtimes)
            if (!Mappings.Any(vm => ReferenceEquals(vm.Runtime, rt)))
                Mappings.Add(new MappingViewModel(rt));
        HasMappings = Mappings.Count > 0;
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
        RefreshedAtText = $"统计时间 {DateTime.Now:HH:mm:ss}";
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
        await AppServices.Manager.StartAllAsync();
        AppServices.SaveSettingsSafe();
    }

    [RelayCommand]
    private async Task StopAllAsync()
    {
        await AppServices.Manager.StopAllAsync();
        AppServices.SaveSettingsSafe();
    }

    [RelayCommand]
    private void RefreshPorts()
    {
        PortNames.ReplaceAll(SerialPortInfo.GetPortNames());
        LocalIpSummary = BuildLocalIpSummary();
        RefreshedAtText = $"串口列表已刷新 {DateTime.Now:HH:mm:ss}";
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var owner = System.Windows.Application.Current.Windows.OfType<Views.MainWindow>().FirstOrDefault();
        new Views.SettingsWindow(owner) { Owner = owner }.ShowDialog();
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
