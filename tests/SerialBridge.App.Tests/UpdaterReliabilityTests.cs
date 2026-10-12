using SerialBridge.App.Services;
using SerialBridge.App.ViewModels;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace SerialBridge.App.Tests;

public sealed class UpdaterReliabilityTests
{
    [Fact]
    public void SaveSkippedVersion_PersistsTheSelectedVersion()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SerialBridge.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "skip_version.txt");

        try
        {
            Assert.True(SkippedVersionStore.Save("1.4.0", path));
            Assert.Equal("1.4.0", SkippedVersionStore.Load(path));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadGate_AllowsOnlyOneConcurrentHolder()
    {
        var gate = new DownloadGate();
        using var start = new ManualResetEventSlim(false);
        using var releaseWinner = new ManualResetEventSlim(false);
        var acquired = 0;

        var attempts = Enumerable.Range(0, 16)
            .Select(_ => Task.Factory.StartNew(() =>
            {
                start.Wait();
                if (!gate.TryAcquire(out var lease)) return;

                Interlocked.Increment(ref acquired);
                releaseWinner.Wait();
                lease.Dispose();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default))
            .ToArray();

        start.Set();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref acquired) == 1, TimeSpan.FromSeconds(2)));
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref acquired));

        releaseWinner.Set();
        await Task.WhenAll(attempts);

        Assert.True(gate.TryAcquire(out var nextLease));
        nextLease.Dispose();
    }

    [Theory]
    [InlineData(true, "CURRENTUSER")]
    [InlineData(false, "ALLUSERS")]
    public void InstallerArguments_CloseTheAppAndRequestUpdateRestart(bool perUser, string installMode)
    {
        var arguments = UpdateService.BuildInstallerArguments(perUser);

        Assert.Contains($"/{installMode}", arguments);
        Assert.Contains("/CLOSEAPPLICATIONS", arguments);
        Assert.Contains("/NORESTARTAPPLICATIONS", arguments);
        Assert.Contains("/SERIALBRIDGEUPDATE=1", arguments);
    }

    [Fact]
    public async Task DownloadInstaller_ValidCompletedFileCanBeReopenedForValidation()
    {
        const int payloadSize = 10 * 1024 * 1024 + 1;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = ServeInstallerOnceAsync(listener, payloadSize);
        var directory = Path.Combine(Path.GetTempPath(), "SerialBridge.Tests", Guid.NewGuid().ToString("N"));
        using var service = new UpdateService(new FakeUpdateEnvironment(), directory);
        var info = new UpdateInfo("1.4.0", $"http://127.0.0.1:{port}/SerialBridge-Setup-1.4.0-x64.exe", "", false);

        try
        {
            var downloadedPath = await service.DownloadInstallerAsync(info, progress: null);

            Assert.NotNull(downloadedPath);
            Assert.True(File.Exists(downloadedPath));
            Assert.Equal(payloadSize, new FileInfo(downloadedPath).Length);
            await serverTask;
        }
        finally
        {
            listener.Stop();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task ServeInstallerOnceAsync(TcpListener listener, int payloadSize)
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

        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Length: {payloadSize}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers);

        var chunk = new byte[81920];
        chunk[0] = (byte)'M';
        chunk[1] = (byte)'Z';
        var remaining = payloadSize;
        var first = true;
        while (remaining > 0)
        {
            var count = Math.Min(chunk.Length, remaining);
            await stream.WriteAsync(chunk.AsMemory(0, count));
            remaining -= count;
            if (first)
            {
                chunk[0] = 0;
                chunk[1] = 0;
                first = false;
            }
        }
    }

    private sealed class FakeUpdateEnvironment : IUpdateEnvironment
    {
        public string CurrentVersion => "1.3.2";
        public bool IsInstalledBuild => true;
        public bool IsPerUserInstall => true;
        public void OpenUrl(string url) { }
    }
}
