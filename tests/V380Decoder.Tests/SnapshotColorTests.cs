using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using V380Decoder.src;
using Xunit;

namespace V380Decoder.Tests;

public class SnapshotColorTests
{
    [Fact]
    public void NativeDecoderBgrPixelsKeepRedAndBlueChannels()
    {
        byte[] bgr = Enumerable.Range(0, 64).SelectMany(_ => new byte[] { 0, 0, 255 }).ToArray();
        using var jpeg = Image.Load<Rgb24>(SnapshotManager.ToJpeg(bgr, 8, 8));
        Assert.True(jpeg[4, 4].R > 230);
        Assert.True(jpeg[4, 4].B < 25);
    }
}
