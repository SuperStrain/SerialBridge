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
    private CancellationTokenSource? _downloadCts;

    public UpdateService(IUpdateEnvironment env, string? downloadDirOverride = null)
    {
        _env = env;
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
            using var resp = await _http.GetAsync(ReleasesApiUrl, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
            if (!root.TryGetProperty("tag_name", out var tagEl)) return null;

            var tag = tagEl.GetString() ?? "";
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

    /// <summary>下载安装包；进度经回调（UI 线程自行调度）。可取消；失败返回 null。</summary>
    public async Task<string?> DownloadInstallerAsync(UpdateInfo info,
        IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(_downloadDir);
            var fileName = Path.GetFileName(new Uri(info.DownloadUrl).LocalPath);
            var target = Path.Combine(_downloadDir, fileName);

            _downloadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var resp = await _http.GetAsync(info.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead, _downloadCts.Token);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(_downloadCts.Token);
            await using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long received = 0;
            var lastReport = DateTime.MinValue;
            int n;
            while ((n = await src.ReadAsync(buffer, _downloadCts.Token)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), _downloadCts.Token);
                received += n;
                if (progress is not null && (DateTime.UtcNow - lastReport).TotalMilliseconds > 100)
                {
                    progress.Report(new DownloadProgress(received, total));
                    lastReport = DateTime.UtcNow;
                }
            }
            progress?.Report(new DownloadProgress(received, total));

            // 基础校验：大小合理且为 PE 文件（MZ 头）
            if (new FileInfo(target).Length < 10 * 1024 * 1024) return null;
            var head = new byte[2];
            await using (var check = File.OpenRead(target))
            {
                if (await check.ReadAsync(head) < 2 || head[0] != 'M' || head[1] != 'Z') return null;
            }
            return target;
        }
        catch (OperationCanceledException)
        {
            return null;   // 用户取消
        }
        catch (Exception ex)
        {
            LogX.Warn($"升级包下载失败：{ex.Message}");
            return null;
        }
    }

    public void CancelDownload()
    {
        try { _downloadCts?.Cancel(); } catch { }
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
