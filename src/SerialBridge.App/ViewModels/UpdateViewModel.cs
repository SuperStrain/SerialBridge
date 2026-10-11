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

    /// <summary>启动后检查（自动模式，静默失败）。返回检查到的版本（供托盘气泡使用）。</summary>
    public async Task<string?> CheckSilentlyAsync()
    {
        var info = await _updater.CheckForUpdateAsync();
        if (info is null) return null;
        if (info.Version == _skippedVersion) return null;
        ShowBanner(info, downloading: false);
        return info.Version;
    }

    /// <summary>手动检查（设置页或横幅刷新），返回给设置页的状态文本。</summary>
    public async Task<string> CheckManuallyAsync()
    {
        var info = await _updater.CheckForUpdateAsync();
        return info is null ? "已是最新版本" : $"新版本 v{info.Version} 可用";
    }

    private void ShowBanner(UpdateInfo info, bool downloading)
    {
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
        UpdateBannerTitle = "正在准备升级…";
        UpdateBannerMessage = "";
        UpdateActionVisible = false;

        // 先检查拿到下载地址
        var info = await _updater.CheckForUpdateAsync();
        if (info is null)
        {
            UpdateBannerVisible = false;
            return;
        }

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
        _skippedVersion = SkippedVersionStore.Consume();
        UpdateBannerVisible = false;
    }
}

/// <summary>跳过版本的记忆（独立存储，供 App 启动时读取）。</summary>
public static class SkippedVersionStore
{
    private static string Path =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SerialBridge", "skip_version.txt");

    public static string? Load()
    {
        try { return File.Exists(Path) ? File.ReadAllText(Path).Trim() : null; }
        catch { return null; }
    }

    public static string? Consume()
    {
        var v = Load();
        try { if (File.Exists(Path)) File.Delete(Path); } catch { }
        return v;
    }
}
