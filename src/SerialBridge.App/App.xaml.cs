using System.Windows;
using SerialBridge.App.Services;
using SerialBridge.App.Views;
using Wpf.Ui.Appearance;

namespace SerialBridge.App;

public partial class App : Application
{
    private IDisposable? _releaseGuard;
    private Task? _shutdownTask;

    internal bool IsShutdownReady { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 同一时刻仅允许一个实例
        if (!SingleInstanceGuard.TryAcquire(out var releaseGuard))
        {
            releaseGuard.Dispose();
            MessageBox.Show("SerialBridge 已经在运行中。", "SerialBridge",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        _releaseGuard = releaseGuard;

        AppServices.Initialize();

        ApplyTheme(AppServices.Settings.General.Theme);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        // 自动恢复映射（UI 显示后再执行，避免阻塞启动）
        _ = AppServices.RestoreMappingsAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            // 托盘退出已经 await 清理；直接 Shutdown 或系统关闭也必须完成清理。
            // AppServices 的任务在后台运行，不会等待当前 UI 上下文。
            AppServices.ShutdownAsync().GetAwaiter().GetResult();
        }
        finally
        {
            _releaseGuard?.Dispose();
            base.OnExit(e);
        }
    }

    internal Task RequestShutdownAsync() => _shutdownTask ??= ShutdownAfterCleanupAsync();

    private async Task ShutdownAfterCleanupAsync()
    {
        await AppServices.ShutdownAsync();
        IsShutdownReady = true;
        Shutdown();
    }

    public static void ApplyTheme(string theme)
    {
        var mode = theme switch
        {
            "Dark" => ApplicationTheme.Dark,
            "Auto" => ApplicationThemeManager.GetSystemTheme().ToString().Contains("Dark")
                ? ApplicationTheme.Dark
                : ApplicationTheme.Light,
            _ => ApplicationTheme.Light,
        };
        ApplicationThemeManager.Apply(mode);
    }
}
