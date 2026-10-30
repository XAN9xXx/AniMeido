using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class ImageDecodeSizingTests
{
    [Theory]
    [InlineData(150, 0, 2, 300)]
    [InlineData(260, 260, 1.5, 390)]
    [InlineData(300, 325.2, 1.25, 407)]
    [InlineData(64, 64, 0, 64)]
    public void DecodeWidth_UsesActualWidthAndDpi(
        double logicalWidth,
        double actualWidth,
        double scale,
        int expected)
        => Assert.Equal(
            expected,
            ManagedImageLoader.CalculateDecodePixelWidth(
                logicalWidth,
                actualWidth,
                scale));

}
