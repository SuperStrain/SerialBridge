using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using SerialBridge.Core.Logging;
using SerialBridge.Core.Update;

namespace SerialBridge.App.Services;

/// <summary>新版本信息。</summary>
public sealed record UpdateInfo(string Version, string DownloadUrl, string ReleasePageUrl, bool IsPortableAssetOnly);

/// <summary>下载进度。</summary>
public sealed record DownloadProgress(long ReceivedBytes, long? TotalBytes)
{
    public int Percent => TotalBytes is > 0 ? (int)(ReceivedBytes * 100 / TotalBytes.Value) : 0;
}

/// <summary>检查结果状态。</summary>
public enum UpdateCheckStatus
{
    UpdateAvailable,
    UpToDate,
    CheckFailed,
}

/// <summary>检查结果：区分"有新版 / 已最新 / 检查失败"，失败附原因供手动检查如实展示。</summary>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Info, string? FailureReason)
{
    public static readonly UpdateCheckResult Latest = new(UpdateCheckStatus.UpToDate, null, null);
    public static UpdateCheckResult Available(UpdateInfo info) => new(UpdateCheckStatus.UpdateAvailable, info, null);
    public static UpdateCheckResult Failed(string reason) => new(UpdateCheckStatus.CheckFailed, null, reason);
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
    /// <summary>主检查源：Release 列表（按版本号取最大，不受发布时间乱序影响）。</summary>
    public const string ReleasesListApiUrl =
        "https://api.github.com/repos/SuperStrain/SerialBridge/releases?per_page=10";
    public const string ReleasesPageUrl =
        "https://github.com/SuperStrain/SerialBridge/releases/latest";

    private readonly HttpClient _http;
    /// <summary>不跟随重定向的客户端：读取 releases/latest 页面 302 的 Location 提取版本（限流兜底）。</summary>
    private readonly HttpClient _pageClient;
    private readonly IUpdateEnvironment _env;
    private readonly string _downloadDir;
    private readonly string _apiUrl;
    private readonly string _fallbackUrl;
    private static readonly DownloadGate SharedDownloadGate = new();

    /// <summary>API 限流退避截止时间（进程级静态：多实例共享同一 IP 配额）。</summary>
    private static DateTimeOffset? _apiLimitedUntil;

    public UpdateService(IUpdateEnvironment env, string? downloadDirOverride = null,
        string? apiUrlOverride = null, string? fallbackUrlOverride = null)
    {
        _env = env;
        _apiUrl = apiUrlOverride ?? ReleasesListApiUrl;
        _fallbackUrl = fallbackUrlOverride ?? ReleasesPageUrl;
        _downloadDir = downloadDirOverride
            ?? Path.Combine(Path.GetTempPath(), "SerialBridge", "update");
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SerialBridge-Updater");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.Timeout = TimeSpan.FromSeconds(15);   // 仅作用于检查；下载用独立请求
        _pageClient = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        _pageClient.DefaultRequestHeaders.UserAgent.ParseAdd("SerialBridge-Updater");
        _pageClient.Timeout = TimeSpan.FromSeconds(10);
    }

    // ---------- 版本比较 ----------

    /// <summary>
    /// 语义化版本比较：b 大于 a 返回 true。容忍 v 前缀与非数字尾缀（1.2.2-beta 按 1.2.2 处理）。
    /// </summary>
    public static bool IsNewerVersion(string current, string candidate) =>
        Core.Update.SemanticVersion.IsNewer(current, candidate);

    // ---------- 版本检查 ----------

    /// <summary>检查最新版本；返回 null 表示当前已是最新或检查失败（静默场景用）。</summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default) =>
        (await CheckUpdateDetailedAsync(ct)).Info;

    /// <summary>
    /// 检查最新版本（详细结果）。主路径用 Release 列表按版本号取最大；
    /// API 限流（403）期间进程级退避并降级走 releases/latest 页面重定向（该路径无 API 配额）。
    /// </summary>
    public async Task<UpdateCheckResult> CheckUpdateDetailedAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            if (_apiLimitedUntil > DateTimeOffset.UtcNow)
            {
                LogX.Info("检查更新：API 限流退避中，直接走网页兜底");
                return await CheckViaWebRedirectAsync(cts.Token);
            }

            using var resp = await _http.GetAsync(_apiUrl, cts.Token);
            LogX.Info($"检查更新：HTTP {(int)resp.StatusCode}");
            if ((int)resp.StatusCode == 403)
            {
                ReadRateLimitReset(resp);
                return await CheckViaWebRedirectAsync(cts.Token);
            }
            if (!resp.IsSuccessStatusCode)
                return await CheckViaWebRedirectAsync(cts.Token);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            if (!ReleaseParsing.TrySelectHighestRelease(doc.RootElement, out var release, out var tag))
                return UpdateCheckResult.Failed("Release 信息解析失败（无有效发布）");

            LogX.Info($"检查更新：远端 {tag}，本地 {_env.CurrentVersion}");
            if (!IsNewerVersion(_env.CurrentVersion, tag)) return UpdateCheckResult.Latest;

            JsonElement? assets = release.TryGetProperty("assets", out var a) &&
                a.ValueKind == JsonValueKind.Array ? a : null;
            return UpdateCheckResult.Available(BuildInfoFromAssets(tag, assets));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return UpdateCheckResult.Failed("检查超时");
        }
        catch (Exception ex)
        {
            LogX.Warn($"检查更新失败：{ex.Message}");
            return UpdateCheckResult.Failed($"网络错误（{ex.Message}）");
        }
    }

    /// <summary>从 Release 的 assets 里找 Setup 安装包，构造 UpdateInfo。</summary>
    private UpdateInfo BuildInfoFromAssets(string tag, JsonElement? assets)
    {
        var version = tag.TrimStart('v', 'V');
        var setupAsset = "SerialBridge-Setup-" + version + "-x64.exe";
        string? downloadUrl = null;

        if (assets is { } arr)
        {
            foreach (var a in arr.EnumerateArray())
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

    /// <summary>
    /// 网页兜底：releases/latest 页面会 302 到 /tag/vX.Y.Z，从 Location 提取最新版本。
    /// 该路径不走 API 配额（共享代理出口被限流时仍可用）。
    /// 注意它按发布时间指向（与 /releases/latest API 同源），仅作限流降级，不是主路径。
    /// </summary>
    private async Task<UpdateCheckResult> CheckViaWebRedirectAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _pageClient.GetAsync(_fallbackUrl, ct);
            var location = resp.Headers.Location?.ToString();
            LogX.Info($"检查更新（网页兜底）：HTTP {(int)resp.StatusCode}，Location {location ?? "无"}");
            if (string.IsNullOrEmpty(location) || !ReleaseParsing.TryParseTagFromRedirect(location, out var tag))
                return UpdateCheckResult.Failed("无法获取最新版本（GitHub API 限流且网页兜底未返回版本）");

            LogX.Info($"检查更新（网页兜底）：远端 {tag}，本地 {_env.CurrentVersion}");
            if (!IsNewerVersion(_env.CurrentVersion, tag)) return UpdateCheckResult.Latest;

            // 兜底路径拿不到 assets 列表，按命名规则构造下载地址；若资产缺失会在下载时失败并提示
            var version = tag.TrimStart('v', 'V');
            var repoBase = _fallbackUrl.EndsWith("/releases/latest", StringComparison.OrdinalIgnoreCase)
                ? _fallbackUrl[..^"/releases/latest".Length]
                : _fallbackUrl;
            var downloadUrl = $"{repoBase}/releases/download/{tag}/SerialBridge-Setup-{version}-x64.exe";
            return UpdateCheckResult.Available(new UpdateInfo(version, downloadUrl, ReleasesPageUrl,
                IsPortableAssetOnly: !_env.IsInstalledBuild));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return UpdateCheckResult.Failed("检查超时");
        }
        catch (Exception ex)
        {
            LogX.Warn($"检查更新（网页兜底）失败：{ex.Message}");
            return UpdateCheckResult.Failed($"网络错误（{ex.Message}）");
        }
    }

    /// <summary>读 x-ratelimit-reset 设置进程级退避窗口；期间不再请求 API（每次 403 也无意义）。</summary>
    private static void ReadRateLimitReset(HttpResponseMessage resp)
    {
        var header = resp.Headers.TryGetValues("x-ratelimit-reset", out var values)
            ? values.FirstOrDefault() : null;
        if (long.TryParse(header, out var epoch))
        {
            var reset = DateTimeOffset.FromUnixTimeSeconds(epoch).AddSeconds(30);   // 小余量，规避时钟偏差
            if (reset > _apiLimitedUntil)
            {
                _apiLimitedUntil = reset;
                LogX.Warn($"检查更新：GitHub API 限流（403），退避至 {reset:HH:mm:ss}，期间走网页兜底");
            }
        }
        else
        {
            LogX.Warn("检查更新：GitHub API 限流（403），本周期走网页兜底");
        }
    }

    // ---------- 下载 ----------

    /// <summary>下载安装包；进度经回调（UI 线程自行调度）。可取消；失败返回 null。</summary>
    public async Task<string?> DownloadInstallerAsync(UpdateInfo info,
        IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        if (!SharedDownloadGate.TryAcquire(out var downloadLease))
        {
            LogX.Warn("升级包正在下载中，忽略重复请求。");
            return null;
        }

        using (downloadLease)
            return await DownloadInstallerCoreAsync(info, progress, ct);
    }

    private async Task<string?> DownloadInstallerCoreAsync(UpdateInfo info,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        // 独立 HttpClient：下载大文件不与检查请求共享连接池，
        // 避免旧响应流悬挂导致的文件/连接占用
        using var downloadClient = new HttpClient();
        downloadClient.Timeout = TimeSpan.FromMinutes(10);
        downloadClient.DefaultRequestHeaders.UserAgent.ParseAdd("SerialBridge-Updater");

        string? tempPath = null;
        try
        {
            Directory.CreateDirectory(_downloadDir);
            var fileName = Path.GetFileName(new Uri(info.DownloadUrl).LocalPath);
            // 下载到唯一临时名：旧文件被占用/未释放时不受影响，完成后原子改名
            var target = Path.Combine(_downloadDir, fileName);
            tempPath = target + $".{Guid.NewGuid():N}.downloading";

            using var resp = await downloadClient.GetAsync(info.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength;
            long received;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                received = 0;
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
            }
            progress?.Report(new DownloadProgress(received, total));

            // 基础校验：大小合理且为 PE 文件（MZ 头）
            if (new FileInfo(tempPath).Length < 10 * 1024 * 1024)
            {
                TryDelete(tempPath);
                return null;
            }
            var head = new byte[2];
            await using (var check = File.OpenRead(tempPath))
            {
                if (await check.ReadAsync(head) < 2 || head[0] != 'M' || head[1] != 'Z')
                {
                    TryDelete(tempPath);
                    return null;
                }
            }

            // 原子替换最终文件；旧文件被占用（残留句柄/杀软扫描）时
            // 直接使用新临时文件启动安装——下载已完成且校验通过，绝不能丢弃
            try
            {
                File.Move(tempPath, target, overwrite: true);
                tempPath = null;
                return target;
            }
            catch (IOException ex)
            {
                LogX.Warn($"升级包替换被占用（{ex.Message}），直接使用新临时文件启动安装。");
                return tempPath;
            }
            catch (UnauthorizedAccessException ex)
            {
                LogX.Warn($"升级包替换被拒绝（{ex.Message}），直接使用新临时文件启动安装。");
                return tempPath;
            }
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            return null;   // 用户取消
        }
        catch (Exception ex)
        {
            LogX.Warn($"升级包下载失败：{ex.Message}");
            TryDelete(tempPath);
            return null;
        }
    }

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
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
            var args = BuildInstallerArguments(_env.IsPerUserInstall);
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

    internal static string BuildInstallerArguments(bool perUserInstall)
    {
        var mode = perUserInstall ? "CURRENTUSER" : "ALLUSERS";
        return $"/SILENT /SUPPRESSMSGBOXES /NORESTART /{mode} " +
               "/CLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /SERIALBRIDGEUPDATE=1";
    }

    /// <summary>打开下载页（绿色版 / 兜底路径）。</summary>
    public void OpenReleasesPage() => _env.OpenUrl(ReleasesPageUrl);

    public void Dispose()
    {
        _http.Dispose();
        _pageClient.Dispose();
    }
}
