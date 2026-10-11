using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using SerialBridge.Core.Logging;

namespace SerialBridge.App.Services;

/// <summary>新版本信息。</summary>
public sealed record UpdateInfo(string Version, string DownloadUrl, string ReleasePageUrl, bool IsPortableAssetOnly);

/// <summary>下载进度。</summary>
public sealed record DownloadProgress(long ReceivedBytes, long? TotalBytes)
{
    public int Percent => TotalBytes is > 0 ? (int)(ReceivedBytes * 100 / TotalBytes.Value) : 0;
}

/// <summary>UpdateService 可测试性注入点。</summary>
public interface IUpdateEnvironment
{
    /// <summary>当前程序版本（如 "1.2.2"）。</summary>
    string CurrentVersion { get; }
    /// <summary>安装版（true）或绿色版（false）。判定：程序目录含 unins000.exe。</summary>
    bool IsInstalledBuild { get; }
    /// <summary>本机安装是否为用户级（LocalAppData）——决定 Setup 传递的权限参数。</summary>
    bool IsPerUserInstall { get; }
    /// <summary>打开 URL（浏览器）。</summary>
    void OpenUrl(string url);
}

/// <summary>
/// 自动升级服务：GitHub Releases 版本检查、安装包下载、静默升级接力。
/// 网络异常一律静默（返回 null），不打扰用户。
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string ReleasesApiUrl =
        "https://api.github.com/repos/SuperStrain/SerialBridge/releases/latest";
    public const string ReleasesPageUrl =
        "https://github.com/SuperStrain/SerialBridge/releases/latest";

    private readonly HttpClient _http;
    private readonly IUpdateEnvironment _env;
    private readonly string _downloadDir;
    private readonly string _apiUrl;
    private CancellationTokenSource? _downloadCts;

    public UpdateService(IUpdateEnvironment env, string? downloadDirOverride = null, string? apiUrlOverride = null)
    {
        _env = env;
        _apiUrl = apiUrlOverride ?? ReleasesApiUrl;
        _downloadDir = downloadDirOverride
            ?? Path.Combine(Path.GetTempPath(), "SerialBridge", "update");
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SerialBridge-Updater");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.Timeout = TimeSpan.FromSeconds(15);   // 仅作用于检查；下载用独立请求
    }

    // ---------- 版本比较 ----------

    /// <summary>
    /// 语义化版本比较：b 大于 a 返回 true。容忍 v 前缀与非数字尾缀（1.2.2-beta 按 1.2.2 处理）。
    /// </summary>
    public static bool IsNewerVersion(string current, string candidate) =>
        Core.Update.SemanticVersion.IsNewer(current, candidate);

    private static bool TryParseVersion(string s, out (int Major, int Minor, int Patch) v)
    {
        v = (0, 0, 0);
        if (string.IsNullOrWhiteSpace(s)) return false;
        var parts = s.Trim().TrimStart('v', 'V').Split('.');
        if (parts.Length < 2) return false;
        if (!int.TryParse(parts[0], out var major)) return false;
        if (!int.TryParse(parts[1], out var minor)) return false;
        var patch = 0;
        if (parts.Length >= 3)
        {
            var p = parts[2];
            var dash = p.IndexOf('-');
            if (dash >= 0) p = p[..dash];
            if (!int.TryParse(p, out patch)) return false;
        }
        v = (major, minor, patch);
        return true;
    }

    // ---------- 版本检查 ----------

    /// <summary>检查最新版本；返回 null 表示当前已是最新或检查失败（静默）。</summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var resp = await _http.GetAsync(_apiUrl, cts.Token);
            LogX.Info($"检查更新：HTTP {(int)resp.StatusCode}");
            if ((int)resp.StatusCode == 403)
            {
                // GitHub API 匿名限流（60 次/小时/IP）：静默跳过，下个周期重试
                LogX.Warn("检查更新：GitHub API 限流（403），本周期跳过");
                return null;
            }
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
            if (!root.TryGetProperty("tag_name", out var tagEl)) return null;

            var tag = tagEl.GetString() ?? "";
            LogX.Info($"检查更新：远端 {tag}，本地 {_env.CurrentVersion}");
            if (!IsNewerVersion(_env.CurrentVersion, tag)) return null;

            var version = tag.TrimStart('v', 'V');
            var setupAsset = "SerialBridge-Setup-" + version + "-x64.exe";
            string? downloadUrl = null;

            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.Equals(name, setupAsset, StringComparison.OrdinalIgnoreCase) &&
                        a.TryGetProperty("browser_download_url", out var u))
                    {
                        downloadUrl = u.GetString();
                        break;
                    }
                }
            }

            // 安装版且找到安装包资产 → 自动升级；否则（绿色版/资产缺失）仅引导下载页
            var autoUpgradable = _env.IsInstalledBuild && downloadUrl is not null;
            return new UpdateInfo(version,
                downloadUrl ?? ReleasesPageUrl,
                ReleasesPageUrl,
                IsPortableAssetOnly: !autoUpgradable);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;   // 超时：静默
        }
        catch (Exception ex)
        {
            LogX.Warn($"检查更新失败：{ex.Message}");
            return null;
        }
    }

    // ---------- 下载 ----------

    /// <summary>下载中防抖标记（并发点击立即升级时直接拒绝）。</summary>
    private volatile bool _downloading;

    /// <summary>下载安装包；进度经回调（UI 线程自行调度）。可取消；失败返回 null。</summary>
    public async Task<string?> DownloadInstallerAsync(UpdateInfo info,
        IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        if (_downloading)
        {
            LogX.Warn("升级包正在下载中，忽略重复请求。");
            return null;
        }
        _downloading = true;
        try
        {
            return await DownloadInstallerCoreAsync(info, progress, ct);
        }
        finally
        {
            _downloading = false;
        }
    }

    private async Task<string?> DownloadInstallerCoreAsync(UpdateInfo info,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        // 独立 HttpClient：下载大文件不与检查请求共享连接池，
        // 避免旧响应流悬挂导致的文件/连接占用
        using var downloadClient = new HttpClient();
        downloadClient.Timeout = TimeSpan.FromMinutes(10);
        downloadClient.DefaultRequestHeaders.UserAgent.ParseAdd("SerialBridge-Updater");

        try
        {
            Directory.CreateDirectory(_downloadDir);
            var fileName = Path.GetFileName(new Uri(info.DownloadUrl).LocalPath);
            // 下载到唯一临时名：旧文件被占用/未释放时不受影响，完成后原子改名
            var target = Path.Combine(_downloadDir, fileName);
            var tmp = target + $".{Guid.NewGuid():N}.downloading";

            using var resp = await downloadClient.GetAsync(info.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long received = 0;
            var lastReport = DateTime.MinValue;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                received += n;
                if (progress is not null && (DateTime.UtcNow - lastReport).TotalMilliseconds > 100)
                {
                    progress.Report(new DownloadProgress(received, total));
                    lastReport = DateTime.UtcNow;
                }
            }
            progress?.Report(new DownloadProgress(received, total));

            // 基础校验：大小合理且为 PE 文件（MZ 头）
            if (new FileInfo(tmp).Length < 10 * 1024 * 1024) return null;
            var head = new byte[2];
            await using (var check = File.OpenRead(tmp))
            {
                if (await check.ReadAsync(head) < 2 || head[0] != 'M' || head[1] != 'Z') return null;
            }

            // 原子替换最终文件；旧文件被占用时直接使用新的临时路径（Setup 从哪里运行都可以）
            try
            {
                File.Move(tmp, target, overwrite: true);
                return target;
            }
            catch (IOException)
            {
                LogX.Warn("旧升级包被占用，使用新临时文件启动安装。");
                return tmp;
            }
        }
        catch (OperationCanceledException)
        {
            CleanupTempDownloads();
            return null;   // 用户取消
        }
        catch (Exception ex)
        {
            LogX.Warn($"升级包下载失败：{ex.Message}");
            CleanupTempDownloads();
            return null;
        }
    }

    /// <summary>清理残留的 .downloading 临时文件。</summary>
    private void CleanupTempDownloads()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_downloadDir, "*.downloading"))
                File.Delete(f);
        }
        catch { /* 清理失败不影响主流程 */ }
    }

    public void CancelDownload()
    {
        // 取消经由传入的 ct 传播（进度取消即中止下载）
    }

    // ---------- 安装接力 ----------

    /// <summary>
    /// 启动已下载的 Setup（静默 + 重启应用）。返回 false 表示启动失败（提示手动运行）。
    /// </summary>
    public bool LaunchSilentSetup(string setupPath)
    {
        try
        {
            var mode = _env.IsPerUserInstall ? "CURRENTUSER" : "ALLUSERS";
            var args = $"/SILENT /SUPPRESSMSGBOXES /NORESTART /{mode} /RESTARTAPPLICATIONS";
            var psi = new ProcessStartInfo(setupPath, args)
            {
                UseShellExecute = true,   // 触发 UAC（ALLUSERS 场景）
            };
            return Process.Start(psi) is not null;
        }
        catch (Exception ex)
        {
            LogX.Warn($"启动升级安装器失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>打开下载页（绿色版 / 兜底路径）。</summary>
    public void OpenReleasesPage() => _env.OpenUrl(ReleasesPageUrl);

    public void Dispose() => _http.Dispose();
}
