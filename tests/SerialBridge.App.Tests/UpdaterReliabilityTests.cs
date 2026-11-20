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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadInstaller_UnavailableTargetPreservesOldFileAndReturnsCompleteExecutable(bool readOnlyTarget)
    {
        const int payloadSize = 10 * 1024 * 1024 + 1;
        const string fileName = "SerialBridge-Setup-1.4.0-x64.exe";
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = ServeInstallerOnceAsync(listener, payloadSize);
        var directory = Path.Combine(Path.GetTempPath(), "SerialBridge.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, fileName);
        var oldContents = Encoding.ASCII.GetBytes("previous installer");
        await File.WriteAllBytesAsync(target, oldContents);
        using var service = new UpdateService(new FakeUpdateEnvironment(), directory);
        var info = new UpdateInfo("1.4.0", $"http://127.0.0.1:{port}/{fileName}", "", false);

        try
        {
            if (readOnlyTarget) File.SetAttributes(target, FileAttributes.ReadOnly);
            using var lockedTarget = readOnlyTarget ? null
                : new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);

            var downloadedPath = await service.DownloadInstallerAsync(info, progress: null);
            await serverTask;

            Assert.NotNull(downloadedPath);
            Assert.True(Path.IsPathFullyQualified(downloadedPath));
            Assert.Equal(".exe", Path.GetExtension(downloadedPath));
            Assert.NotEqual(target, downloadedPath);
            Assert.Equal(directory, Path.GetDirectoryName(downloadedPath));
            var expectedPayload = new byte[payloadSize];
            expectedPayload[0] = (byte)'M';
            expectedPayload[1] = (byte)'Z';
            Assert.Equal(expectedPayload, await File.ReadAllBytesAsync(downloadedPath));
            Assert.Equal(oldContents, await File.ReadAllBytesAsync(target));
            Assert.Empty(Directory.GetFiles(directory, "*.downloading"));
        }
        finally
        {
            listener.Stop();
            if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CancelDownload_StopsTransferDeletesPartialFileAndReleasesGate()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var directory = Path.Combine(Path.GetTempPath(), "SerialBridge.Tests",
            Guid.NewGuid().ToString("N"));
        using var service = new UpdateService(new FakeUpdateEnvironment(), directory);
        var info = new UpdateInfo("1.4.0",
            $"http://127.0.0.1:{port}/SerialBridge-Setup-1.4.0-x64.exe", "", false);
        using var cancel = new CancellationTokenSource();
        using var retryCancel = new CancellationTokenSource();
        using var unblock = new CancellationTokenSource();
        using var retryListener = new TcpListener(IPAddress.Loopback, 0);
        Task<string?>? download = null;
        Task<string?>? retryDownload = null;
        Task? retryServer = null;
        var bodyReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync(unblock.Token);
            await using var stream = accepted.GetStream();
            var last = 0;
            while (last < 4)
            {
                var value = stream.ReadByte();
                if (value < 0) return;
                last = (last, value) switch
                {
                    (0, '\r') => 1, (1, '\n') => 2,
                    (2, '\r') => 3, (3, '\n') => 4,
                    _ => 0
                };
            }
            var header = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 20000000\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, unblock.Token);
            await stream.WriteAsync(new byte[81920], unblock.Token);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, unblock.Token); }
            catch (OperationCanceledException) { }
        });

        try
        {
            // Progress is throttled to one event per 100 ms and FileStream
            // may buffer its first write. Use progress only to establish that
            // the client has entered body processing; poll the file independently.
            var progress = new SynchronousProgress(p =>
            {
                if (p.ReceivedBytes > 0) bodyReceived.TrySetResult();
            });
            download = service.DownloadInstallerAsync(info, progress, cancel.Token);
            await bodyReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            var written = false;
            while (DateTime.UtcNow < deadline)
            {
                written = Directory.Exists(directory) &&
                    Directory.GetFiles(directory, "*.downloading")
                        .Any(file => new FileInfo(file).Length > 0);
                if (written) break;
                await Task.Delay(25);
            }
            Assert.True(written, "Expected a non-empty .downloading file before cancel.");
            cancel.Cancel();
            Assert.Null(await download.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Empty(Directory.GetFiles(directory, "*.downloading"));

            // A successful second transfer, not a second null result, proves
            // that cancellation released the process-wide DownloadGate.
            const int completeSize = 10 * 1024 * 1024 + 1;
            retryListener.Start();
            var retryPort = ((IPEndPoint)retryListener.LocalEndpoint).Port;
            retryServer = ServeInstallerOnceAsync(retryListener, completeSize);
            var retryInfo = info with
            {
                DownloadUrl = $"http://127.0.0.1:{retryPort}/SerialBridge-Setup-1.4.0-x64.exe"
            };
            retryDownload = service.DownloadInstallerAsync(retryInfo, null, retryCancel.Token);
            var completed = await retryDownload.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotNull(completed);
            Assert.True(File.Exists(completed));
            Assert.Equal(completeSize, new FileInfo(completed).Length);
            await retryServer.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(Directory.GetFiles(directory, "*.downloading"));
        }
        finally
        {
            // WaitAsync timeout does not cancel the underlying download.
            // Stop both clients BEFORE deleting Windows-exclusive temp files.
            cancel.Cancel();
            retryCancel.Cancel();
            unblock.Cancel();
            listener.Stop();
            retryListener.Stop();
            if (download is not null)
            {
                try { await download.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception) { /* Preserve the original test failure. */ }
            }
            if (retryDownload is not null)
            {
                try { await retryDownload.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception) { /* Preserve the original test failure. */ }
            }
            try { await server.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) { /* Preserve the original test failure. */ }
            if (retryServer is not null)
            {
                try { await retryServer.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception) { /* Preserve the original test failure. */ }
            }
            // Do not race a still-running task holding FileShare.None.
            if ((download is null || download.IsCompleted) &&
                (retryDownload is null || retryDownload.IsCompleted) &&
                Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UpdateDialogCloseAlwaysCancelsActiveDownload()
    {
        var sourcePath = Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "UpdatePopupWindow.cs");
        // The popup is a WPF source fixture; assert that both Cancel and X/Alt+F4
        // go through window close, and window close cancels the view-model attempt.
        var source = File.ReadAllText(sourcePath);
        Assert.Contains("cancelButton.Click += (_, _) => Close();", source);
        Assert.Contains("Closed += (_, _) =>", source);
        Assert.Contains("_update.CancelUpdate();", source);
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

    private sealed class SynchronousProgress(Action<DownloadProgress> callback)
        : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => callback(value);
    }

    private sealed class FakeUpdateEnvironment : IUpdateEnvironment
    {
        public string CurrentVersion => "1.3.2";
        public bool IsInstalledBuild => true;
        public bool IsPerUserInstall => true;
        public void OpenUrl(string url) { }
    }
}
