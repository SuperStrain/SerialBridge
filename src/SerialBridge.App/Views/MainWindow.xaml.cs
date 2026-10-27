using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        // 最小化：保留任务栏图标（普通最小化行为），不再隐藏进托盘
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        var app = (App)Application.Current;
        if (app.IsShutdownReady) return;

        // 消息循环保持运行直到异步清理完成，再由 Application.Shutdown 关闭窗口。
        if (_exitFromTray)
        {
            e.Cancel = true;
            IsEnabled = false;
            await app.RequestShutdownAsync();
            return;
        }

        // 点击关闭按钮：隐藏到系统托盘（映射继续运行），首次给出气泡提示
        e.Cancel = true;
        Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _trayIcon?.ShowNotification("SerialBridge 仍在运行",
                "窗口已收起到系统托盘（屏幕右下角时钟区域），映射保持运行。点击托盘图标可恢复窗口，彻底退出请用托盘右键菜单的“退出”。");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Shutdown();
        _trayIcon?.Dispose();
        base.OnClosed(e);
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

    private void OnOpenLinuxAccess(object sender, RoutedEventArgs e)
        => LinuxAccessWindow.Open(this);

    private void OnCaptureBadgeClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MappingViewModel vm }
            && vm.OpenCaptureFolderCommand.CanExecute(null))
            vm.OpenCaptureFolderCommand.Execute(null);
    }

    private void OnOpenGitHub(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法打开浏览器：" + ex.Message, "SerialBridge",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 开机自启（--minimized）时以最小化状态出现在任务栏
        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--minimized"))
            WindowState = WindowState.Minimized;
    }
}
