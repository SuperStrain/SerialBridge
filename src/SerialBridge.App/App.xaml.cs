using System.Windows;
using SerialBridge.App.Services;
using SerialBridge.App.Views;
using Wpf.Ui.Appearance;

namespace SerialBridge.App;

public partial class App : Application
{
    private IDisposable? _releaseGuard;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 同一时刻仅允许一个实例
        if (!SingleInstanceGuard.TryAcquire(out var releaseGuard))
        {
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
        AppServices.ShutdownAsync().Wait(TimeSpan.FromSeconds(8));
        _releaseGuard?.Dispose();
        base.OnExit(e);
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
