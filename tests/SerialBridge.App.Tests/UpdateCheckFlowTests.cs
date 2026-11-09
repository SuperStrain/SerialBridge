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
    public async Task SilentCheck_WithoutReleaseBody_UsesFallbackText()
    {
        var (service, server) = StartApiServer(NoBodyJson);
        try
        {
            var viewModel = new UpdateViewModel(service, skippedVersion: null);
            var version = await viewModel.CheckSilentlyAsync();

            Assert.Equal("9.9.9", version);
            Assert.Contains("发布页", viewModel.ReleaseNotes);
            await server;
        }
        finally
        {
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
