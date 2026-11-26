using System.Net;
using System.Net.Sockets;
using System.Text;
using SerialBridge.App.Services;
using SerialBridge.App.ViewModels;
using Xunit;

namespace SerialBridge.App.Tests;

/// <summary>
/// 更新检查流程端到端测试：本地 TcpListener 模拟 GitHub Releases API，
/// 覆盖静默检查的版本/Release 说明透传、跳过版本与无正文兜底（30 分钟周期复查共用此路径）。
/// </summary>
public sealed class UpdateCheckFlowTests
{
    private static string InlineText(System.Windows.Documents.Inline inline) => inline switch
    {
        System.Windows.Documents.Run run => run.Text,
        System.Windows.Documents.LineBreak => "\n",
        System.Windows.Documents.Span span => string.Concat(span.Inlines.Select(InlineText)),
        _ => ""
    };

    private const string NewVersionJson = """
        [
          {
            "tag_name": "v9.9.9",
            "draft": false,
            "prerelease": false,
            "body": "### 修复问题\n- 修复串口停止超时\n### 新增功能\n- 快捷命令栏",
            "assets": [
              { "name": "SerialBridge-Setup-9.9.9-x64.exe", "browser_download_url": "http://127.0.0.1/SerialBridge-Setup-9.9.9-x64.exe" }
            ]
          }
        ]
        """;

    private const string NoBodyJson = """
        [
          { "tag_name": "v9.9.9", "draft": false, "prerelease": false, "assets": [] }
        ]
        """;

    private const string OldVersionJson = """
        [
          { "tag_name": "v1.0.0", "draft": false, "prerelease": false, "assets": [] }
        ]
        """;

    private sealed class FakeUpdateEnvironment(string currentVersion) : IUpdateEnvironment
    {
        public string CurrentVersion => currentVersion;
        public bool IsInstalledBuild => true;
        public bool IsPerUserInstall => true;
        public void OpenUrl(string url) { }
    }

    private static (UpdateService Service, Task Server) StartApiServer(string json)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var service = new UpdateService(
            new FakeUpdateEnvironment("1.3.2"),
            downloadDirOverride: Path.Combine(Path.GetTempPath(), "SerialBridge.Tests", Guid.NewGuid().ToString("N")),
            apiUrlOverride: $"http://127.0.0.1:{port}/releases");
        return (service, ServeJsonOnceAsync(listener, json));
    }

    private static (UpdateService Service, Task Server, TcpListener Listener, CancellationTokenSource ServerCancellation)
        StartApiServerWithFeed(string json, int feedStatus, string feed)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var baseUrl = $"http://127.0.0.1:{port}";
        var service = new UpdateService(
            new FakeUpdateEnvironment("1.3.2"),
            downloadDirOverride: Path.Combine(Path.GetTempPath(), "SerialBridge.Tests", Guid.NewGuid().ToString("N")),
            apiUrlOverride: $"{baseUrl}/releases",
            fallbackUrlOverride: $"{baseUrl}/releases/latest");
        var serverCancellation = new CancellationTokenSource();
        return (service, ServeJsonAndFeedAsync(listener, json, feedStatus, feed, serverCancellation.Token),
            listener, serverCancellation);
    }

    private static async Task ServeJsonAndFeedAsync(
        TcpListener listener, string json, int feedStatus, string feed, CancellationToken cancellationToken)
    {
        try
        {
            await RespondOnceAsync(listener, 200, "OK", json, "application/json; charset=utf-8", cancellationToken);
            await RespondOnceAsync(listener, feedStatus, feedStatus == 200 ? "OK" : "Not Found",
                feed, "application/atom+xml; charset=utf-8", cancellationToken);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static (UpdateService Service, Task Server, TcpListener Listener, CancellationTokenSource ServerCancellation) StartRateLimitedFallbackServer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var baseUrl = $"http://127.0.0.1:{port}";
        var service = new UpdateService(
            new FakeUpdateEnvironment("1.3.2"),
            downloadDirOverride: Path.Combine(Path.GetTempPath(), "SerialBridge.Tests", Guid.NewGuid().ToString("N")),
            apiUrlOverride: $"{baseUrl}/releases",
            fallbackUrlOverride: $"{baseUrl}/releases/latest");
        var feed = $"""
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <id>{baseUrl}/releases/tag/v9.9.8</id>
                <link rel="alternate" href="{baseUrl}/releases/tag/v9.9.8" />
                <title>SerialBridge v9.9.8</title>
                <content type="html">&lt;p&gt;旧版本内容，不应显示。&lt;/p&gt;</content>
              </entry>
              <entry>
                <id>{baseUrl}/releases/tag/v9.9.9</id>
                <link rel="alternate" href="{baseUrl}/releases/tag/v9.9.9" />
                <title>SerialBridge v9.9.9</title>
                <content type="html">
                  &lt;h1&gt;SerialBridge v9.9.9&lt;/h1&gt;&lt;p&gt;本版本包含问题修复。&lt;/p&gt;
                  &lt;h2&gt;问题修复&lt;/h2&gt;&lt;ul&gt;&lt;li&gt;&lt;strong&gt;修复串口停止超时&lt;/strong&gt;（&lt;a href=&quot;https://github.com/SuperStrain/SerialBridge/pull/49&quot;&gt;变更记录&lt;/a&gt;）&lt;/li&gt;&lt;/ul&gt;
                  &lt;p&gt;参数 &amp;lt;port&amp;gt; 应保留。&lt;/p&gt;
                  &lt;pre&gt;&lt;code&gt;if x:
                run()
                  &lt;/code&gt;&lt;/pre&gt;
                </content>
              </entry>
            </feed>
            """;
        var serverCancellation = new CancellationTokenSource();
        return (service, ServeRateLimitedFallbackAsync(listener, port, feed, serverCancellation.Token),
            listener, serverCancellation);
    }

    private static async Task ServeRateLimitedFallbackAsync(
        TcpListener listener, int port, string feed, CancellationToken cancellationToken)
    {
        try
        {
            await RespondOnceAsync(listener, 403, "Forbidden", "", "application/json", cancellationToken);
            await RespondOnceAsync(listener, 302, "Found", "", "text/plain", cancellationToken,
                $"http://127.0.0.1:{port}/releases/tag/v9.9.9");
            await RespondOnceAsync(listener, 200, "OK", feed, "application/atom+xml; charset=utf-8", cancellationToken);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task RespondOnceAsync(
        TcpListener listener, int status, string reason, string body, string contentType,
        CancellationToken cancellationToken, string? location = null)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        var terminator = 0;
        var oneByte = new byte[1];
        while (terminator < 4)
        {
            var count = await stream.ReadAsync(oneByte.AsMemory(0, 1), cancellationToken);
            if (count == 0) throw new IOException("Client closed before sending HTTP headers.");
            var value = oneByte[0];
            terminator = (terminator, value) switch
            {
                (0, '\r') => 1,
                (1, '\n') => 2,
                (2, '\r') => 3,
                (3, '\n') => 4,
                _ => 0,
            };
        }

        var payload = Encoding.UTF8.GetBytes(body);
        var locationHeader = location is null ? "" : $"Location: {location}\r\n";
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {payload.Length}\r\n{locationHeader}Connection: close\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
    }

    private static async Task ServeJsonOnceAsync(TcpListener listener, string json)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        var terminator = 0;
        while (terminator < 4)
        {
            var value = stream.ReadByte();
            if (value < 0) return;
            terminator = (terminator, value) switch
            {
                (0, '\r') => 1,
                (1, '\n') => 2,
                (2, '\r') => 3,
                (3, '\n') => 4,
                _ => 0,
            };
        }

        var payload = Encoding.UTF8.GetBytes(json);
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers);
        await stream.WriteAsync(payload);
    }

    [Fact]
    public async Task SilentCheck_SurfacesNewVersionWithReleaseNotes()
    {
        var (service, server) = StartApiServer(NewVersionJson);
        try
        {
            var viewModel = new UpdateViewModel(service, skippedVersion: null);
            var version = await viewModel.CheckSilentlyAsync();

            Assert.Equal("9.9.9", version);
            Assert.True(viewModel.UpdateBadgeVisible);
            Assert.Equal("新版本 v9.9.9", viewModel.UpdatePopupTitle);
            Assert.Contains("修复串口停止超时", viewModel.ReleaseNotes);
            Assert.Contains("快捷命令栏", viewModel.ReleaseNotes);
            Assert.Contains("发现新版本 v9.9.9", viewModel.UpdateBadgeVersion);
            await server;
        }
        finally
        {
            service.Dispose();
        }
    }

    [Fact]
    public async Task CheckDetailed_WhenApiIsRateLimited_LoadsReleaseNotesFromMatchingAtomEntry()
    {
        var (service, server, listener, serverCancellation) = StartRateLimitedFallbackServer();
        var rateLimitField = typeof(UpdateService).GetField(
            "_apiLimitedUntil",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        rateLimitField?.SetValue(null, null);

        try
        {
            var viewModel = new UpdateViewModel(service, skippedVersion: null);
            var version = await viewModel.CheckSilentlyAsync();

            Assert.Equal("9.9.9", version);
            Assert.True(viewModel.UpdateBadgeVisible);
            Assert.Equal("新版本 v9.9.9", viewModel.UpdatePopupTitle);
            Assert.Contains("修复串口停止超时", viewModel.ReleaseNotes);
            Assert.Contains("## 问题修复", viewModel.ReleaseNotes);
            Assert.Contains("https://github.com/SuperStrain/SerialBridge/pull/49", viewModel.ReleaseNotes);
            Assert.DoesNotContain("旧版本内容", viewModel.ReleaseNotes);
            Assert.Contains("&lt;port&gt;", viewModel.ReleaseNotes);
            Assert.Contains("    run()", viewModel.ReleaseNotes);
            Assert.Contains(new string((char)96, 3), viewModel.ReleaseNotes);

            var document = ReleaseNotesRenderer.ToFlowDocument(viewModel.ReleaseNotes);
            var renderedText = string.Concat(document.Blocks.OfType<System.Windows.Documents.Paragraph>()
                .Select(paragraph => string.Concat(paragraph.Inlines.Select(InlineText))));
            Assert.Contains("<port>", renderedText);
            Assert.Contains("    run()", renderedText);
            await server;
        }
        finally
        {
            serverCancellation.Cancel();
            listener.Stop();
            try { await server.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
            serverCancellation.Dispose();
            service.Dispose();
            rateLimitField?.SetValue(null, null);
        }
    }

    [Fact]
    public async Task SilentCheck_HonorsSkippedVersion()
    {
        var (service, server) = StartApiServer(NewVersionJson);
        try
        {
            var viewModel = new UpdateViewModel(service, skippedVersion: "9.9.9");
            var version = await viewModel.CheckSilentlyAsync();

            Assert.Null(version);
            Assert.False(viewModel.UpdateBadgeVisible);
            await server;
        }
        finally
        {
            service.Dispose();
        }
    }

    [Fact]
    public async Task SilentCheck_WithoutReleaseBody_UsesFallbackTextWhenFeedUnavailable()
    {
        var (service, server, listener, serverCancellation) =
            StartApiServerWithFeed(NoBodyJson, 404, "");
        try
        {
            var viewModel = new UpdateViewModel(service, skippedVersion: null);
            var version = await viewModel.CheckSilentlyAsync();

            Assert.Equal("9.9.9", version);
            Assert.Contains("暂时无法加载", viewModel.ReleaseNotes);
            Assert.Contains("前往发布", viewModel.ReleaseNotes);
            await server;
        }
        finally
        {
            serverCancellation.Cancel();
            listener.Stop();
            try { await server.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
            serverCancellation.Dispose();
            service.Dispose();
        }
    }

    [Fact]
    public async Task SilentCheck_WhenApiOmitsBody_RecoversReleaseNotesFromAtomFeed()
    {
        var (service, server, listener, serverCancellation) =
            StartApiServerWithFeed(NoBodyJson, 200, $"""
                <feed xmlns="http://www.w3.org/2005/Atom">
                  <entry>
                    <id>http://127.0.0.1/releases/tag/v9.9.9</id>
                    <link rel="alternate" href="http://127.0.0.1/releases/tag/v9.9.9" />
                    <title>SerialBridge v9.9.9</title>
                    <content type="html">&lt;h2&gt;问题修复&lt;/h2&gt;&lt;ul&gt;&lt;li&gt;API 正文缺失时仍能显示说明&lt;/li&gt;&lt;/ul&gt;</content>
                  </entry>
                </feed>
                """);
        try
        {
            var viewModel = new UpdateViewModel(service, skippedVersion: null);
            var version = await viewModel.CheckSilentlyAsync();

            Assert.Equal("9.9.9", version);
            Assert.True(viewModel.UpdateBadgeVisible);
            Assert.Contains("API 正文缺失时仍能显示说明", viewModel.ReleaseNotes);
            Assert.DoesNotContain("暂时无法加载", viewModel.ReleaseNotes);
            await server;
        }
        finally
        {
            serverCancellation.Cancel();
            listener.Stop();
            try { await server.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
            serverCancellation.Dispose();
            service.Dispose();
        }
    }

    [Fact]
    public async Task CheckDetailed_WhenRemoteIsOlder_ReportsUpToDate()
    {
        var (service, server) = StartApiServer(OldVersionJson);
        try
        {
            var result = await service.CheckUpdateDetailedAsync();

            Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
            Assert.Null(result.Info);
            await server;
        }
        finally
        {
            service.Dispose();
        }
    }
}
