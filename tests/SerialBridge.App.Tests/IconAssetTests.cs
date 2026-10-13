using System.Buffers.Binary;
using Xunit;

namespace SerialBridge.App.Tests;

public sealed class IconAssetTests
{
    private static readonly int[] RequiredSizes = [16, 20, 24, 32, 48, 64, 128, 256];
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    [Fact]
    public void ApplicationIcon_HasDedicatedPngFrameForEveryWindowsSize()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "app.ico");
        var data = File.ReadAllBytes(path);
        Assert.True(data.Length >= 6);
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0, 2)));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2, 2)));

        var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4, 2));
        var frames = Enumerable.Range(0, count).Select(index => ReadFrame(data, index)).ToArray();

        foreach (var size in RequiredSizes)
        {
            var frame = Assert.Single(frames, item => item.Width == size && item.Height == size);
            Assert.Equal(32, frame.BitsPerPixel);
            Assert.True(data.AsSpan(frame.Offset, PngSignature.Length).SequenceEqual(PngSignature),
                $"{size}x{size} 图标帧不是 PNG 格式");
        }
    }

    private static IconFrame ReadFrame(byte[] data, int index)
    {
        var entry = data.AsSpan(6 + index * 16, 16);
        var width = entry[0] == 0 ? 256 : entry[0];
        var height = entry[1] == 0 ? 256 : entry[1];
        var bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(6, 2));
        var offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(12, 4)));
        return new IconFrame(width, height, bitsPerPixel, offset);
    }

    private sealed record IconFrame(int Width, int Height, int BitsPerPixel, int Offset);
}
