using System.Collections.Concurrent;
using System.Reflection;
using System.Windows;
using SerialBridge.App.Services;
using SerialBridge.Core.Bridge;
using SerialBridge.Core.Config;
using SerialBridge.Core.Management;
using SerialBridge.Core.Models;
using Xunit;

namespace SerialBridge.App.Tests;

[CollectionDefinition("App lifecycle", DisableParallelization = true)]
public sealed class AppLifecycleCollection;

[Collection("App lifecycle")]
public sealed class AppLifecycleTests
{
    [Fact]
    public async Task ShutdownWithoutInitialization_LeavesExistingConfigurationUntouched()
    {
        using var services = new IsolatedServices(initialized: false);
        var original = File.ReadAllBytes(services.ConfigPath);

        await AppServices.ShutdownAsync();

        Assert.Equal(original, File.ReadAllBytes(services.ConfigPath));
    }

    [Fact]
    public async Task ShutdownFromUiContext_CompletesWithoutDispatchingToBlockedUi()
    {
        using var services = new IsolatedServices(initialized: true);
        var (runtime, releaseStop) = services.CreatePendingMapping();
        var context = new QueuedContext();
        var previous = SynchronizationContext.Current;
        Task shutdown;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            shutdown = AppServices.ShutdownAsync();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        releaseStop.SetResult();
        try
        {
            var finished = await Task.WhenAny(shutdown, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(shutdown, finished);
            await shutdown;
            Assert.Equal(MappingState.Stopped, runtime.State);
            Assert.Single(new ConfigStore(services.ConfigPath).Load().Mappings);
        }
        finally
        {
            await context.DrainUntilCompletedAsync(shutdown);
        }
    }

    [Fact]
    public async Task ShutdownDuringUiInitiatedStop_CompletesWithoutPumpingUi()
    {
        using var services = new IsolatedServices(initialized: true);
        var (runtime, releaseWriter) = services.CreatePendingMapping();
        releaseWriter.SetResult();
        var releaseSerial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(SerialBridge.Core.Serial.SerialPortDevice)
            .GetField("_sessionTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(runtime.Serial, releaseSerial.Task);
        var context = new QueuedContext();
        var previous = SynchronizationContext.Current;
        Task stop;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            stop = runtime.StopAsync();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        var shutdown = AppServices.ShutdownAsync();
        releaseSerial.SetResult();
        try
        {
            Assert.Same(shutdown, await Task.WhenAny(shutdown, Task.Delay(TimeSpan.FromSeconds(2))));
            await shutdown;
            Assert.Equal(MappingState.Stopped, runtime.State);
        }
        finally
        {
            await context.DrainUntilCompletedAsync(Task.WhenAll(stop, shutdown));
        }
    }

    [Fact]
    public async Task ConcurrentShutdownRequests_ShareOneCleanupAndStopEachMappingOnce()
    {
        using var services = new IsolatedServices(initialized: true);
        var (runtime, releaseStop) = services.CreatePendingMapping();
        var stopNotifications = 0;
        runtime.StatusChanged += mapping =>
        {
            if (mapping.State == MappingState.Stopped)
                Interlocked.Increment(ref stopNotifications);
        };

        var shutdowns = Enumerable.Range(0, 8).Select(_ => AppServices.ShutdownAsync()).ToArray();
        releaseStop.SetResult();
        await Task.WhenAll(shutdowns);

        Assert.All(shutdowns, shutdown => Assert.Same(shutdowns[0], shutdown));
        Assert.Equal(1, stopNotifications);
        Assert.Single(new ConfigStore(services.ConfigPath).Load().Mappings);
    }

    [Fact]
    public async Task OnExitFallback_WaitsForCleanupWithoutNeedingUiDispatcher()
    {
        using var services = new IsolatedServices(initialized: true);
        var (runtime, releaseStop) = services.CreatePendingMapping();
        var context = new QueuedContext();
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new ExitTestApplication();
                SynchronizationContext.SetSynchronizationContext(context);
                entered.SetResult();
                app.ExitForTest();
                Assert.Equal(MappingState.Stopped, runtime.State);
                Assert.Single(new ConfigStore(services.ConfigPath).Load().Mappings);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                exited.SetResult();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(SpinWait.SpinUntil(
            () => (thread.ThreadState & ThreadState.WaitSleepJoin) != 0,
            TimeSpan.FromSeconds(2)));
        releaseStop.SetResult();
        try
        {
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(12));
            Assert.Null(failure);
        }
        finally
        {
            await context.DrainUntilCompletedAsync(AppServices.ShutdownAsync());
            thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    private sealed class ExitTestApplication : SerialBridge.App.App
    {
        public void ExitForTest() => OnExit((ExitEventArgs)Activator.CreateInstance(
            typeof(ExitEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [0], culture: null)!);
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public async Task DrainUntilCompletedAsync(Task task)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
            {
                while (_queue.TryDequeue(out var work)) work.Callback(work.State);
                await Task.Delay(10);
            }
            await task.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    private sealed class IsolatedServices : IDisposable
    {
        private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.NonPublic;
        private readonly Dictionary<FieldInfo, object?> _originalFields;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "SerialBridge.Tests", Guid.NewGuid().ToString("N"));
        public string ConfigPath => Path.Combine(_directory, "config.json");

        public IsolatedServices(bool initialized)
        {
            _originalFields = typeof(AppServices).GetFields(StaticFlags)
                .Where(field => !field.IsInitOnly && !field.IsLiteral)
                .ToDictionary(field => field, field => field.GetValue(null));
            Directory.CreateDirectory(_directory);
            File.WriteAllText(ConfigPath, "{\"General\":{\"Theme\":\"Dark\"},\"Mappings\":[{\"Name\":\"保留用户映射\",\"PortName\":\"COM99\"}]}");
            SetField("<ConfigStore>k__BackingField", new ConfigStore(ConfigPath));
            SetField("<Settings>k__BackingField", new AppSettings());
            SetField("<Manager>k__BackingField", new MappingManager());
            SetField("_initialized", initialized);
            typeof(AppServices).GetField("_shutdownTask", StaticFlags)?.SetValue(null, null);
        }

        public (MappingRuntime Runtime, TaskCompletionSource Release) CreatePendingMapping()
        {
            var runtime = AppServices.Manager.CreateMapping(new MappingConfig { Name = "待退出映射" });
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(MappingRuntime).GetField("_state", instanceFlags)!.SetValue(runtime, MappingState.Running);
            typeof(MappingRuntime).GetField("_serialWriterTask", instanceFlags)!.SetValue(runtime, release.Task);
            return (runtime, release);
        }

        private static void SetField(string name, object value) =>
            typeof(AppServices).GetField(name, StaticFlags)!.SetValue(null, value);

        public void Dispose()
        {
            foreach (var (field, value) in _originalFields) field.SetValue(null, value);
            Directory.Delete(_directory, recursive: true);
        }
    }
}
