using System.Diagnostics;
using System.Text;
using SerialBridge.Core.Models;
using Xunit;

namespace SerialBridge.Core.Tests;

public class CommandMacroTests
{
    [Theory]
    [InlineData("None", "status")]
    [InlineData("CR", "status\r")]
    [InlineData("LF", "status\n")]
    [InlineData("CRLF", "status\r\n")]
    public void TextEncoding_RespectsLineEnding(string ending, string expected)
    {
        var data = CommandMacroRunner.Encode(new CommandStep
        {
            Format = "Text", Content = "status", Ending = ending
        });
        Assert.Equal(expected, Encoding.UTF8.GetString(data));
    }

    [Fact]
    public void HexEncoding_IsByteExact()
    {
        var data = CommandMacroRunner.Encode(new CommandStep
        {
            Format = "Hex", Content = "01 03\n00 ff", Ending = "None"
        });
        Assert.Equal(new byte[] { 1, 3, 0, 255 }, data);
    }

    [Theory]
    [InlineData("00 0")]
    [InlineData("ZZ")]
    [InlineData("")]
    public void InvalidHex_RejectsInput(string text)
    {
        Assert.Throws<FormatException>(() => CommandMacroRunner.Encode(
            new CommandStep { Format = "Hex", Content = text, Ending = "None" }));
    }

    [Fact]
    public async Task Macro_ValidatesAllStepsBeforeSendingAnything()
    {
        var sent = new List<byte[]>();
        var macro = new CommandPreset { Steps =
        [
            new CommandStep { Content = "hello" },
            new CommandStep { Format = "Hex", Content = "ZZ", Ending = "None" }
        ] };
        await Assert.ThrowsAsync<FormatException>(() => CommandMacroRunner.RunAsync(
            macro, bytes => { sent.Add(bytes); return true; }, CancellationToken.None));
        Assert.Empty(sent);
    }

    [Fact]
    public async Task Macro_SendsStepsInOrder()
    {
        var sent = new List<string>();
        var macro = new CommandPreset { Steps =
        [
            new CommandStep { Content = "echo one", Ending = "CR" },
            new CommandStep { Content = "echo two", Ending = "CRLF" }
        ] };
        await CommandMacroRunner.RunAsync(macro, bytes =>
        {
            sent.Add(Encoding.UTF8.GetString(bytes)); return true;
        }, CancellationToken.None);
        Assert.Equal(new[] { "echo one\r", "echo two\r\n" }, sent);
    }

    [Fact]
    public async Task Macro_StopsAfterQueueRejection()
    {
        var count = 0;
        var macro = new CommandPreset { Steps =
        [
            new CommandStep { Content = "first" },
            new CommandStep { Content = "second" },
            new CommandStep { Content = "third" }
        ] };
        await Assert.ThrowsAsync<IOException>(() => CommandMacroRunner.RunAsync(
            macro, _ => ++count < 2, CancellationToken.None));
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Macro_CancelDuringDelay_PreventsNextStep()
    {
        using var cancel = new CancellationTokenSource();
        var count = 0;
        var macro = new CommandPreset { Steps =
        [
            new CommandStep { Content = "one", DelayMs = 5000 },
            new CommandStep { Content = "two" }
        ] };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CommandMacroRunner.RunAsync(macro, _ =>
            {
                count++;
                cancel.Cancel();
                return true;
            }, cancel.Token));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Macro_TrailingDelayOnSingleStep_DoesNotBlockCompletion()
    {
        var sent = new List<byte[]>();
        var macro = new CommandPreset { Steps =
        [
            new CommandStep { Content = "uname -a", DelayMs = 5000 }
        ] };
        var stopwatch = Stopwatch.StartNew();
        await CommandMacroRunner.RunAsync(macro, bytes =>
        {
            sent.Add(bytes); return true;
        }, CancellationToken.None);
        Assert.Single(sent);
        Assert.True(stopwatch.ElapsedMilliseconds < 2000,
            $"尾步延时没有下一步可节流，不应拖住调用方（实际等待 {stopwatch.ElapsedMilliseconds}ms）");
    }
}
