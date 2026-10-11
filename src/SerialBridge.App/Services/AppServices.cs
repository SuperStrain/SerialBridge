using System.Threading;
using System.Threading.Tasks;
using SerialBridge.Core.Bridge;
using SerialBridge.Core.Config;
using SerialBridge.Core.Logging;
using SerialBridge.Core.Management;
using SerialBridge.Core.Models;

namespace SerialBridge.App.Services;

/// <summary>
/// 全局服务容器（应用规模小，采用显式单例而非依赖注入框架）。
/// </summary>
public static class AppServices
{
    public const string GitHubUrl = "https://github.com/SuperStrain/SerialBridge";

    /// <summary>程序集版本（x.y.z），界面展示用，避免多处硬编码。</summary>
    public static string AppVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

    private static readonly SemaphoreSlim _saveLock = new(1, 1);

    public static AppSettings Settings { get; private set; } = new();
    public static ConfigStore ConfigStore { get; private set; } = new();
    public static MappingManager Manager { get; private set; } = new();

    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;
        LogService.Initialize();
        ConfigStore = new ConfigStore();
        Settings = ConfigStore.Load();
        Manager = new MappingManager();
        _initialized = true;

        StartupHelper.SetLaunchOnStartup(Settings.General.LaunchOnStartup);
        LogX.Info($"全局服务初始化完成（主题：{Settings.General.Theme}）");
    }

    /// <summary>
    /// 启动时恢复所有已保存的映射；其中勾选了“自动运行”且允许恢复的映射自动启动。
    /// </summary>
    public static async Task RestoreMappingsAsync()
    {
        foreach (var cfg in Settings.Mappings)
        {
            var rt = Manager.CreateMapping(cfg);
            if (Settings.General.RestoreMappingsOnStartup && cfg.AutoStart)
            {
                var error = await Manager.StartMappingAsync(rt);
                if (error is not null)
                    LogX.Warn($"映射「{cfg.Name}」自动启动失败：{error}");
            }
        }
        var count = Settings.Mappings.Count;
        if (count > 0)
            LogX.Info($"已恢复 {count} 条映射配置。");
    }

    /// <summary>保存全部配置（映射列表 + 偏好）。</summary>
    public static void SaveSettings()
    {
        Settings.Mappings = Manager.Mappings.Select(m => m.Config).ToList();
        ConfigStore.Save(Settings);
    }

    /// <summary>静默保存（失败只记日志，不打断 UI 操作）。</summary>
    public static void SaveSettingsSafe()
    {
        try
        {
            SaveSettings();
        }
        catch (Exception ex)
        {
            LogX.Error("配置自动保存失败：" + ex.Message, ex);
        }
    }

    /// <summary>退出前的清理：停所有映射、保存配置、刷新日志。</summary>
    public static async Task ShutdownAsync()
    {
        try
        {
            await Manager.StopAllAsync();
            SaveSettings();
        }
        catch (Exception ex)
        {
            LogX.Error("退出清理异常：" + ex.Message, ex);
        }
        finally
        {
            LogService.Shutdown();
        }
    }
}

/// <summary>防止多开（命名互斥体）。</summary>
public static class SingleInstanceGuard
{
    private const string MutexName = "Global\\SerialBridge.SingleInstance";
    private static Mutex? _mutex;

    public static bool TryAcquire(out IDisposable release)
    {
        _mutex = new Mutex(true, MutexName, out var createdNew);
        release = new Releaser(_mutex, createdNew);
        return createdNew;
    }

    private sealed class Releaser(Mutex mutex, bool owned) : IDisposable
    {
        public void Dispose()
        {
            if (owned) { try { mutex.ReleaseMutex(); } catch { } }
            mutex.Dispose();
        }
    }
}
