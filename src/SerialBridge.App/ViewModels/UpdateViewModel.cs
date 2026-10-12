using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SerialBridge.App.Services;
using SerialBridge.Core.Logging;

namespace SerialBridge.App.ViewModels;

/// <summary>自动升级状态机：检查 → 提示 → 下载（进度）→ 安装接力。</summary>
public partial class UpdateViewModel : ObservableObject
{
    private readonly UpdateService _updater;
    private string? _skippedVersion;

    [ObservableProperty] private bool _updateBannerVisible;
    [ObservableProperty] private string _updateBannerTitle = "";
    [ObservableProperty] private string _updateBannerMessage = "";
    [ObservableProperty] private bool _updateActionVisible;   // 立即升级/跳过按钮（下载中隐藏）

    public UpdateViewModel(UpdateService updater, string? skippedVersion)
    {
        _updater = updater;
        _skippedVersion = skippedVersion;
    }

    public UpdateService Updater => _updater;

    /// <summary>启动后检查（自动模式，静默失败）。返回检查到的版本（供托盘气泡使用）。</summary>
    public async Task<string?> CheckSilentlyAsync()
    {
        var info = await _updater.CheckForUpdateAsync();
        if (info is null) return null;
        if (info.Version == _skippedVersion) return null;
        ShowBanner(info, downloading: false);
        return info.Version;
    }

    private UpdateInfo? _cachedInfo;

    private void ShowBanner(UpdateInfo info, bool downloading)
    {
        _cachedInfo = info;
        UpdateBannerTitle = $"新版本 v{info.Version} 可用";
        UpdateBannerMessage = downloading
            ? "正在下载升级包…"
            : info.IsPortableAssetOnly
                ? "绿色版请从 GitHub 下载新版本"
                : "升级将自动完成并重启程序";
        UpdateBannerVisible = true;
        UpdateActionVisible = !downloading;
    }

    [RelayCommand]
    public async Task UpdateNowAsync()
    {
        LogX.Info($"升级点击：触发，缓存信息 {(_cachedInfo is null ? "无" : "v" + _cachedInfo.Version + " " + _cachedInfo.DownloadUrl)}");
        var info = _cachedInfo;
        if (info is null)
        {
            // 无缓存（罕见：横幅未经过检查流程）才回退到在线检查
            info = await _updater.CheckForUpdateAsync();
            if (info is null) return;
        }

        UpdateBannerTitle = "正在准备升级…";
        UpdateBannerMessage = "";
        UpdateActionVisible = false;

        if (info.IsPortableAssetOnly)
        {
            _updater.OpenReleasesPage();
            return;
        }

        var progress = new Progress<DownloadProgress>(p =>
        {
            UpdateBannerTitle = $"正在下载 v{info.Version}… {p.Percent}%";
            UpdateBannerMessage = p.TotalBytes is null ? "" : $"{p.ReceivedBytes / 1048576.0:F1} / {p.TotalBytes / 1048576.0:F1} MB";
        });

        UpdateBannerTitle = $"正在下载 v{info.Version}… 0%";
        var setupPath = await _updater.DownloadInstallerAsync(info, progress);
        if (setupPath is null)
        {
            UpdateBannerTitle = "升级下载失败";
            UpdateBannerMessage = "可点击此提示旁方式手动下载";
            UpdateActionVisible = true;
            return;
        }

        UpdateBannerTitle = "下载完成，即将安装并重启…";
        UpdateBannerMessage = "";
        await Task.Delay(800);   // 让用户看到状态

        if (!_updater.LaunchSilentSetup(setupPath))
        {
            UpdateBannerTitle = "升级程序启动失败";
            UpdateBannerMessage = $"请手动运行：{setupPath}";
            return;
        }
        // Setup 会关闭本程序并自动重启——此处无需退出动作
    }

    public void SkipVersion()
    {
        if (_cachedInfo is not null)
        {
            _skippedVersion = _cachedInfo.Version;
            SkippedVersionStore.Save(_skippedVersion);
        }
        UpdateBannerVisible = false;
    }
}

/// <summary>跳过版本的记忆（独立存储，供 App 启动时读取）。</summary>
public static class SkippedVersionStore
{
    private static string DefaultPath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SerialBridge", "skip_version.txt");

    public static string? Load() => Load(DefaultPath);

    internal static string? Load(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }

    public static bool Save(string version) => Save(version, DefaultPath);

    internal static bool Save(string version, string path)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;

        var tempPath = path + ".tmp";
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(tempPath, version.Trim());
            File.Move(tempPath, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            LogX.Warn($"保存跳过版本失败：{ex.Message}");
            return false;
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }
}
