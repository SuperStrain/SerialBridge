using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using SerialBridge.App.Services;
using SerialBridge.Core.Logging;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace SerialBridge.App.Views;

/// <summary>主窗口内嵌的设置页面，直接读写全局 Settings（属性 setter 即时保存）。</summary>
public partial class SettingsPage : UserControl
{
    private readonly SettingsViewModel _viewModel = new();

    public SettingsPage()
    {
        InitializeComponent();
        _viewModel.UpdateService = new UpdateService(new AppUpdateEnvironment());
        _viewModel.AutoCheckUpdates = AppServices.Settings.General.AutoCheckUpdates;
        DataContext = _viewModel;
    }

    private void OnOpenLogDir(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(LogService.LogDir);
            Process.Start(new ProcessStartInfo(LogService.LogDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开日志目录：{ex.Message}", "设置",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOpenGitHub(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppServices.GitHubUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法打开浏览器：" + ex.Message, "设置",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

/// <summary>设置 ViewModel：直接读写全局 Settings，关闭时由属性 setter 保存。</summary>
public partial class SettingsViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private readonly SerialBridge.Core.Models.GeneralSettings _general;
    private bool _synchronizingTheme;

    public SettingsViewModel()
    {
        _general = AppServices.Settings.General;
        _themeIndex = _general.Theme switch { "Dark" => 1, "Auto" => 2, _ => 0 };
        App.ThemeChanged += SynchronizeTheme;
        _restoreMappingsOnStartup = _general.RestoreMappingsOnStartup;
        _allowLanDiscovery = _general.AllowLanDiscovery;
    }

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private int _themeIndex;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _launchOnStartup = StartupHelper.IsLaunchOnStartupEnabled();
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _restoreMappingsOnStartup;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _allowLanDiscovery;
    private bool _restoringDiscoverySetting;
    private int _discoveryChangeVersion;

    partial void OnAllowLanDiscoveryChanged(bool value)
    {
        if (!_restoringDiscoverySetting) _ = ApplyDiscoveryAsync(value, ++_discoveryChangeVersion);
    }

    private async Task ApplyDiscoveryAsync(bool value, int version)
    {
        try { await AppServices.SetLanDiscoveryEnabledAsync(value); }
        catch (Exception ex)
        {
            // Ignore stale failures if a newer user choice is already queued.
            if (version != _discoveryChangeVersion) return;
            MessageBox.Show("局域网发现服务切换失败：" + ex.Message, "设置",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            _restoringDiscoverySetting = true;
            try { AllowLanDiscovery = _general.AllowLanDiscovery; }
            finally { _restoringDiscoverySetting = false; }
        }
    }
    public string LogDirText => LogService.LogDir;
    public string AppVersion => AppServices.AppVersion;

    public Services.UpdateService? UpdateService { get; set; }

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _autoCheckUpdates = true;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string _updateStatusText = "";

    partial void OnAutoCheckUpdatesChanged(bool value)
    {
        _general.AutoCheckUpdates = value;
        AppServices.SaveSettingsSafe();
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (UpdateService is null) return;
        UpdateStatusText = "检查中…";
        var result = await UpdateService.CheckUpdateDetailedAsync();
        UpdateStatusText = result.Status switch
        {
            UpdateCheckStatus.UpdateAvailable => $"新版本 v{result.Info!.Version} 可用" +
                (result.Info.IsPortableAssetOnly ? "（请从 GitHub 下载）" : "（可在主界面横幅升级）"),
            UpdateCheckStatus.UpToDate => "已是最新版本",
            _ => $"检查失败：{result.FailureReason}",
        };
    }

    private void SynchronizeTheme(string theme)
    {
        var index = theme switch { "Dark" => 1, "Auto" => 2, _ => 0 };
        if (ThemeIndex == index) return;
        _synchronizingTheme = true;
        try { ThemeIndex = index; }
        finally { _synchronizingTheme = false; }
    }

    partial void OnThemeIndexChanged(int value)
    {
        if (_synchronizingTheme) return;
        var theme = value switch { 1 => "Dark", 2 => "Auto", _ => "Light" };
        if (_general.Theme != theme)
        {
            _general.Theme = theme;
            App.ApplyTheme(theme);
            AppServices.SaveSettingsSafe();
        }
    }

    partial void OnLaunchOnStartupChanged(bool value)
    {
        StartupHelper.SetLaunchOnStartup(value);
        _general.LaunchOnStartup = value;
        AppServices.SaveSettingsSafe();
    }

    partial void OnRestoreMappingsOnStartupChanged(bool value)
    {
        _general.RestoreMappingsOnStartup = value;
        AppServices.SaveSettingsSafe();
    }
}
