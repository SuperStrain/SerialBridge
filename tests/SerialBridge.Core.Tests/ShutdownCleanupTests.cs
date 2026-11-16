using SerialBridge.Core.Management;
using Xunit;

namespace SerialBridge.Core.Tests;

public sealed class ShutdownCleanupTests
{
    [Fact]
    public async Task SaveFailure_DoesNotSkipDiscoveryRemotesOrMappings()
    {
        var calls = new List<string>();
        var errors = new List<Exception>();
        await ShutdownCleanup.RunAsync(
            () =>
            {
                calls.Add("save");
                throw new IOException("Settings directory is read-only");
            },
            () => { calls.Add("discovery"); return Task.CompletedTask; },
            () => { calls.Add("remotes"); return Task.CompletedTask; },
            () => { calls.Add("mappings"); return Task.CompletedTask; },
            errors.Add);

        Assert.Equal(new[] { "save", "discovery", "remotes", "mappings" }, calls);
        Assert.Single(errors);
        Assert.IsType<IOException>(errors[0]);
    }

    [Fact]
    public async Task CleanupFailure_DoesNotPreventLaterStages()
    {
        var calls = new List<string>();
        var errors = new List<Exception>();
        await ShutdownCleanup.RunAsync(
            () => calls.Add("save"),
            () => { calls.Add("discovery"); throw new InvalidOperationException("UDP close failed"); },
            () => { calls.Add("remotes"); return Task.FromException(new IOException("TCP close failed")); },
            () => { calls.Add("mappings"); return Task.CompletedTask; },
            errors.Add);

        Assert.Equal(new[] { "save", "discovery", "remotes", "mappings" }, calls);
        Assert.Collection(errors,
            error => Assert.IsType<InvalidOperationException>(error),
            error => Assert.IsType<IOException>(error));
    }
}
