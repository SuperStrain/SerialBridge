using System.Diagnostics;
using System.IO;
using SerialBridge.Core.Logging;

namespace SerialBridge.App.Services;

/// <summary>UpdateService 的真实环境实现。</summary>
public sealed class AppUpdateEnvironment : IUpdateEnvironment
{
    public string CurrentVersion => AppServices.AppVersion;

    public bool IsInstalledBuild =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    public bool IsPerUserInstall
    {
        get
        {
            // 安装在 LocalAppData（用户级）则 CURRENTUSER，否则（Program Files 等）ALLUSERS
            var baseDir = AppContext.BaseDirectory;
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return baseDir.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogX.Warn($"打开链接失败：{ex.Message}");
        }
    }
}
