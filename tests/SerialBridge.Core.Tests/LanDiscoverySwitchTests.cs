using SerialBridge.Core.Tcp;
using Xunit;

namespace SerialBridge.Core.Tests;

public sealed class LanDiscoverySwitchTests
{
    [Fact]
    public async Task RapidDisableEnable_PersistsLastChoiceAfterPreviousStopCompletes()
    {
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<string>();
        var saved = true;
        var listening = true;
        var controller = new LanDiscoverySwitch(
            () => { listening = true; events.Add("start"); },
            async () =>
            {
                events.Add("stop-begin");
                stopEntered.TrySetResult();
                await releaseStop.Task;
                listening = false;
                events.Add("stop-end");
            },
            enabled => { saved = enabled; events.Add($"save-{enabled}"); });

        var disable = controller.SetEnabledAsync(false);
        await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var enable = controller.SetEnabledAsync(true);
        Assert.False(enable.IsCompleted);
        Assert.True(listening);
        releaseStop.SetResult();
        await Task.WhenAll(disable, enable).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(listening);
        Assert.True(saved);
        Assert.Equal(new[] { "stop-begin", "stop-end", "save-False", "start", "save-True" }, events);
    }

    [Fact]
    public async Task RapidDisableEnableDisable_EndsDisabledWithMatchingSavedState()
    {
        var running = false;
        var persisted = false;
        var controller = new LanDiscoverySwitch(
            () => running = true,
            () => { running = false; return Task.CompletedTask; },
            value => persisted = value);

        await controller.SetEnabledAsync(true);
        await Task.WhenAll(
            controller.SetEnabledAsync(false),
            controller.SetEnabledAsync(true),
            controller.SetEnabledAsync(false));
        Assert.False(running);
        Assert.False(persisted);
    }

    [Fact]
    public async Task Shutdown_WaitsForToggleAndDoesNotOverwriteStartupPreference()
    {
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = true;
        var stopCount = 0;
        var controller = new LanDiscoverySwitch(
            () => { },
            async () =>
            {
                stopCount++;
                if (stopCount == 1)
                {
                    stopEntered.TrySetResult();
                    await releaseStop.Task;
                }
            },
            value => saved = value);
        var stopping = controller.SetEnabledAsync(false);
        await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var shutdown = controller.StopForShutdownAsync();
        releaseStop.SetResult();
        await Task.WhenAll(stopping, shutdown);
        await controller.SetEnabledAsync(true);
        Assert.False(saved);
        Assert.Equal(2, stopCount);
    }
}
