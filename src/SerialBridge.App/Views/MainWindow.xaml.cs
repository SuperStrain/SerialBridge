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

    /// <summary>
    /// 行操作显现等级 0-5：0 只显示"···"菜单，5 显示全部五个快捷图标且隐藏"···"。
    /// 由映射表格宽度驱动（窄窗口收纳进菜单，拉宽后渐进显现），供行模板绑定。
    /// </summary>
    public static readonly DependencyProperty RowActionsLevelProperty = DependencyProperty.Register(
        nameof(RowActionsLevel), typeof(int), typeof(MainWindow), new PropertyMetadata(0));

    public int RowActionsLevel
    {
        get => (int)GetValue(RowActionsLevelProperty);
        private set => SetValue(RowActionsLevelProperty, value);
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        Loaded += (_, _) => InitializeTray();
        Closing += OnWindowClosing;
        StateChanged += OnStateChanged;
    }

    /// <summary>
    /// 行操作显现等级 0-4：0 只显示"···"菜单，4 显示全部快捷图标且隐藏"···"。
    /// （客户端计数已并入常驻操作组；渐进项为 监控/日志/编辑/删除 四个图标。）
    /// 以"窗口宽度"为唯一输入，阈值保证各档位内容最小宽 ≤ 当前窗口宽。
    /// </summary>
    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RowActionsLevel = e.NewSize.Width switch
        {
            >= 1220 => 4,
            >= 1170 => 3,
            >= 1120 => 2,
            >= 1090 => 1,
            _ => 0,
        };
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

    // ---------- 更新弹窗 ----------

    private void OnUpdateBadgeClick(object sender, MouseButtonEventArgs e)
    {
        var update = _viewModel.Update;
        if (update is null) return;
        new UpdatePopupWindow(update) { Owner = this }.ShowDialog();
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
        PageRemote.StopRefresh();
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
        // ApplicationThemeManager 在切换时会为窗口写入本地 Background 值，
        // 覆盖掉 XAML 的 DynamicResource；切回浅色后旧深色值会残留（标题栏发黑）。
        // 这里在其处理完成后重新挂回资源引用，让背景继续跟随主题。
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            ClearValue(BackgroundProperty);
            SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        }));
        var icon = FindName("ThemeIcon") as Wpf.Ui.Controls.SymbolIcon;
        if (icon is not null)
            icon.Symbol = next == "Dark"
                ? Wpf.Ui.Controls.SymbolRegular.WeatherSunny24
                : Wpf.Ui.Controls.SymbolRegular.WeatherMoon24;
        AppServices.SaveSettingsSafe();
    }

    // ---------- 页面导航 ----------

    // 远程连接与设置不再弹窗，统一在主界面右侧工作台内切换页面。
    private void ShowPage(string title, string subtitle, UIElement page, Wpf.Ui.Controls.Button activeNav)
    {
        PageTitle.Text = title;
        PageSubtitle.Text = subtitle;

        PageDashboard.Visibility = page == PageDashboard ? Visibility.Visible : Visibility.Collapsed;
        PageRemote.Visibility = page == PageRemote ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = page == PageSettings ? Visibility.Visible : Visibility.Collapsed;

        NavMappings.Appearance = activeNav == NavMappings
            ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Transparent;
        NavRemote.Appearance = activeNav == NavRemote
            ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Transparent;
        NavSettings.Appearance = activeNav == NavSettings
            ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Transparent;
    }

    private void OnNavDashboard(object sender, RoutedEventArgs e)
        => ShowPage("串口映射", "将本地串口转换为 TCP 服务，支持局域网远程访问与 AI Agent 调试",
            PageDashboard, NavMappings);

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        ShowPage("设置", "主题、启动、局域网发现与更新等偏好设置", PageSettings, NavSettings);
        // 设置可能改变托盘相关行为，顺手保持托盘状态最新
        UpdateTrayStatus();
    }

    private void OnOpenRemoteConnections(object sender, RoutedEventArgs e)
        => ShowPage("远程 TCP 连接", "连接同一局域网内的 SerialBridge 或其他原始 TCP 串口服务；终端关闭后连接仍会保持。",
            PageRemote, NavRemote);

    // Linux access remains a usage guide, not a connection workflow.
    // Keep it under Help so it cannot be mistaken for a live Linux session.
    private void OnOpenHelp(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu
        {
            PlacementTarget = sender as UIElement,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        var linux = new System.Windows.Controls.MenuItem { Header = "Linux 连接说明（文字教程）" };
        linux.Click += (_, _) => LinuxAccessWindow.Open(this);
        // “关于”统一由设置页承载，帮助菜单只保留使用类入口。
        menu.Items.Add(linux);
        menu.IsOpen = true;
    }

    private void OnShowLocalIps(object sender, RoutedEventArgs e)
    {
        if (LocalIpPopup is { } popup) popup.IsOpen = !popup.IsOpen;
    }

    private void OnMappingMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.ContextMenu is not { } menu) return;
        menu.DataContext = element.DataContext;
        menu.PlacementTarget = element;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        // 菜单只列出尚未在行内显现为图标的操作：等级 N 时前 N 项隐藏
        var index = 0;
        foreach (var item in menu.Items)
        {
            if (item is System.Windows.Controls.MenuItem menuItem)
                menuItem.Visibility = index < RowActionsLevel
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            index++;
        }
        menu.IsOpen = true;
    }

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
