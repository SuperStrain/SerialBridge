using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using SerialBridge.App.Services;
using SerialBridge.App.ViewModels;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace SerialBridge.App.Views;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel = new();
    private TaskbarIcon? _trayIcon;
    private bool _exitFromTray;
    private static bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        Loaded += (_, _) => InitializeTray();
        Closing += OnWindowClosing;
        StateChanged += OnStateChanged;
    }

    // ---------- 系统托盘 ----------

    private void InitializeTray()
    {
        _trayIcon = (TaskbarIcon)FindResource("TrayIcon")!;
        _trayIcon.ForceCreate();
        UpdateTrayStatus();
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.RunningCount) or nameof(MainViewModel.TotalCount))
                UpdateTrayStatus();
        };
    }

    private void UpdateTrayStatus()
    {
        if (_trayIcon is null) return;
        _trayIcon.ToolTipText =
            $"SerialBridge — {_viewModel.StatusSummary}\n客户端连接：{_viewModel.TotalClients}";
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && AppServices.Settings.General.MinimizeToTray)
        {
            Hide();
            // 首次最小化进托盘时提示去向，避免用户误以为程序已退出
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _trayIcon?.ShowNotification("SerialBridge 仍在运行",
                    "已最小化到系统托盘（屏幕右下角时钟区域），映射保持运行。点击托盘图标可恢复窗口。");
            }
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        // 设置为“关闭时隐藏到托盘”且不是从托盘菜单退出时，隐藏窗口
        if (AppServices.Settings.General.CloseToTray && !_exitFromTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        if (_exitFromTray) return;

        // 直接关闭：确认（有映射运行时提示）
        if (AppServices.Manager.RunningCount > 0)
        {
            var result = MessageBox.Show(
                $"当前有 {AppServices.Manager.RunningCount} 条映射正在运行，退出将断开所有串口与 TCP 连接。\n确定退出吗？",
                "退出 SerialBridge", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        _viewModel.Shutdown();
        _trayIcon?.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    // ---------- 托盘菜单 ----------

    private void OnTrayShowClick(object sender, RoutedEventArgs e)
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private async void OnTrayStartAllClick(object sender, RoutedEventArgs e)
    {
        await AppServices.Manager.StartAllAsync();
        AppServices.SaveSettingsSafe();
    }

    private async void OnTrayStopAllClick(object sender, RoutedEventArgs e)
    {
        await AppServices.Manager.StopAllAsync();
        AppServices.SaveSettingsSafe();
    }

    private void OnTrayExitClick(object sender, RoutedEventArgs e)
    {
        _exitFromTray = true;
        Close();
    }

    private void OnTrayIconClick(object sender, RoutedEventArgs e) => OnTrayShowClick(sender, e);

    // ---------- 页头按钮 ----------

    private void OnToggleTheme(object sender, RoutedEventArgs e)
    {
        var current = AppServices.Settings.General.Theme;
        var next = current == "Dark" ? "Light" : "Dark";
        AppServices.Settings.General.Theme = next;
        App.ApplyTheme(next);
        var icon = FindName("ThemeIcon") as Wpf.Ui.Controls.SymbolIcon;
        if (icon is not null)
            icon.Symbol = next == "Dark"
                ? Wpf.Ui.Controls.SymbolRegular.WeatherSunny24
                : Wpf.Ui.Controls.SymbolRegular.WeatherMoon24;
        AppServices.SaveSettingsSafe();
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow(this) { Owner = this };
        settings.ShowDialog();
        // 设置可能改变了关闭行为，刷新托盘
        UpdateTrayStatus();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 支持 --minimized 启动（开机自启时直接进托盘）
        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--minimized") && AppServices.Settings.General.MinimizeToTray)
            WindowState = WindowState.Minimized;
    }
}
