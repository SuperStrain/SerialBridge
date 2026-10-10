using System.IO;
using Serilog;
using Serilog.Events;

namespace SerialBridge.Core.Logging;

/// <summary>日志初始化与全局访问（Serilog 静态 Log 的封装）。</summary>
public static class LogService
{
    private static bool _initialized;

    /// <summary>默认数据目录：%AppData%\SerialBridge</summary>
    public static string AppDataDir { get; private set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SerialBridge");

    public static string LogDir => Path.Combine(AppDataDir, "logs");

    public static string ConfigFilePath => Path.Combine(AppDataDir, "config.json");

    public static void Initialize(string? dataDirOverride = null)
    {
        if (_initialized) return;
        if (!string.IsNullOrEmpty(dataDirOverride)) AppDataDir = dataDirOverride;

        Directory.CreateDirectory(LogDir);
        PurgeOldLogs();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(LogDir, "serialbridge-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 31,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                shared: true)
            .CreateLogger();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Fatal(e.ExceptionObject as Exception, "未处理异常（进程将终止）");
        _initialized = true;
        Log.Information("==== SerialBridge 启动，数据目录 {Dir} ====", AppDataDir);
    }

    public static void Shutdown()
    {
        Log.Information("==== SerialBridge 退出 ====");
        Log.CloseAndFlush();
    }

    private static void PurgeOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-30);
            foreach (var f in Directory.EnumerateFiles(LogDir, "serialbridge-*.log"))
                if (File.GetLastWriteTime(f) < cutoff)
                    File.Delete(f);
        }
        catch { /* 清理失败不影响启动 */ }
    }
}

/// <summary>简洁的日志门面，避免各模块直接依赖 Serilog 命名空间。</summary>
public static class LogX
{
    public static void Info(string msg) => Log.Information("{Msg}", msg);
    public static void Warn(string msg) => Log.Warning("{Msg}", msg);
    public static void Error(string msg, Exception? ex = null)
    {
        if (ex is null) Log.Error("{Msg}", msg);
        else Log.Error(ex, "{Msg}", msg);
    }
    public static void Debug(string msg) => Log.Debug("{Msg}", msg);
}
