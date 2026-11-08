using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SerialBridge.App.Services;
using SerialBridge.Core.Logging;

namespace SerialBridge.App.ViewModels;

/// <summary>自动升级状态机：检查 → NEW 徽章 → 弹窗 → 下载（进度）→ 安装接力。</summary>
public partial class UpdateViewModel : ObservableObject
{
    private readonly UpdateService _updater;
    private string? _skippedVersion;

    /// <summary>检测到新版本且未被跳过时，主界面标题旁常驻绿色 NEW 徽章。</summary>
    [ObservableProperty] private bool _updateBadgeVisible;
    /// <summary>NEW 徽章的悬停提示。</summary>
    [ObservableProperty] private string _updateBadgeVersion = "";
    /// <summary>更新弹窗标题（新版本 vX.Y.Z）。</summary>
    [ObservableProperty] private string _updatePopupTitle = "";
    /// <summary>更新弹窗展示的 Release 正文（Markdown 源文本；无正文时显示兜底文案）。</summary>
    [ObservableProperty] private string _releaseNotes = "";
    /// <summary>弹窗内的状态行（准备/下载进度/失败/完成）。</summary>
    [ObservableProperty] private string _popupStatusText = "";
    /// <summary>弹窗内「更新」「跳过此版本」按钮可用状态（下载中禁用）。</summary>

    [ObservableProperty] private bool _popupActionVisible = true;

    public UpdateViewModel(UpdateService updater, string? skippedVersion)
    {
        _updater = updater;
        _skippedVersion = skippedVersion;
    }

    public UpdateService Updater => _updater;

    private UpdateInfo? _cachedInfo;

    /// <summary>
    /// 启动 30 秒与每 30 分钟的静默检查入口。返回检查到的版本（跳过版本/无更新/失败均为 null）。
    /// </summary>
    public async Task<string?> CheckSilentlyAsync()
    {
        var info = await _updater.CheckForUpdateAsync();
        if (info is null) return null;
        if (info.Version == _skippedVersion) return null;
        ShowBadge(info);
        return info.Version;
    }

    private void ShowBadge(UpdateInfo info)
    {
        _cachedInfo = info;
        ReleaseNotes = info.ReleaseNotes ?? "（发布页未提供更新说明，请前往发布页查看。）";
        UpdatePopupTitle = $"新版本 v{info.Version}";
        UpdateBadgeVersion = $"发现新版本 v{info.Version}，点击查看更新内容";
        UpdateBadgeVisible = true;
    }

    [RelayCommand]
    public async Task UpdateNowAsync()
    {
        LogX.Info($"升级点击：触发，缓存信息 {(_cachedInfo is null ? "无" : "v" + _cachedInfo.Version + " " + _cachedInfo.DownloadUrl)}");
        var info = _cachedInfo;
        if (info is null)
        {
            // 无缓存（罕见：弹窗未经过检查流程）才回退到在线检查
            info = await _updater.CheckForUpdateAsync();
            if (info is null)
            {
                PopupStatusText = "检查更新失败，请稍后重试或前往发布页下载。";
                return;
            }
            ShowBadge(info);
        }

        PopupStatusText = "正在准备升级…";
        PopupActionVisible = false;

        if (info.IsPortableAssetOnly)
        {
            _updater.OpenReleasesPage();
            PopupStatusText = "绿色版请从发布页手动下载新版本。";
            PopupActionVisible = true;
            return;
        }

        var progress = new Progress<DownloadProgress>(p =>
        {
            PopupStatusText = p.TotalBytes is null
                ? $"正在下载 v{info.Version}… {p.Percent}%"
                : $"正在下载 v{info.Version}… {p.Percent}%（{p.ReceivedBytes / 1048576.0:F1} / {p.TotalBytes / 1048576.0:F1} MB）";
        });

        PopupStatusText = $"正在下载 v{info.Version}… 0%";
        var setupPath = await _updater.DownloadInstallerAsync(info, progress);
        if (setupPath is null)
        {
            PopupStatusText = "升级下载失败，可前往发布页手动下载，或稍后重试。";
            PopupActionVisible = true;
            return;
        }

        PopupStatusText = "下载完成，即将安装并重启…";
        await Task.Delay(800);   // 让用户看到状态

        if (!_updater.LaunchSilentSetup(setupPath))
        {
            PopupStatusText = $"升级程序启动失败，请手动运行：{setupPath}";
            PopupActionVisible = true;
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
        UpdateBadgeVisible = false;
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
