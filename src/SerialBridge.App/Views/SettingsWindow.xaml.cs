using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using SerialBridge.App.Services;
using SerialBridge.Core.Logging;
using SerialBridge.Core.Models;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace SerialBridge.App.Views;

public partial class SettingsWindow : FluentWindow
{
    private readonly SettingsViewModel _viewModel = new();

    public SettingsWindow(Window? owner)
    {
        InitializeComponent();
        Owner = owner;
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

    private void OnClose(object sender, RoutedEventArgs e) => Close();

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

/// <summary>设置窗口 ViewModel：直接读写全局 Settings，关闭时由属性 setter 保存。</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly GeneralSettings _general;

    public SettingsViewModel()
    {
        _general = AppServices.Settings.General;
        _themeIndex = _general.Theme switch { "Dark" => 1, "Auto" => 2, _ => 0 };
        _restoreMappingsOnStartup = _general.RestoreMappingsOnStartup;
    }

    [ObservableProperty] private int _themeIndex;
    [ObservableProperty] private bool _launchOnStartup = StartupHelper.IsLaunchOnStartupEnabled();
    [ObservableProperty] private bool _restoreMappingsOnStartup;
    public string LogDirText => LogService.LogDir;
    public string AppVersion => AppServices.AppVersion;

    partial void OnThemeIndexChanged(int value)
    {
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
