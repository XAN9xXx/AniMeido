namespace AniMeido.Tests;

[Trait("Category", "Mechanism")]
public sealed class AnimeCardDragTokenLifetimeTests
{
    [Fact]
    public void PngStreamIsOwnedByTheCompleteDragBitmapOperation()
    {
        var source = File.ReadAllText(Path.Combine(AppDataRootsTests.RepositoryRoot,
            "Plugins", "AniMeido.Plugin.Base", "Services", "AnimeCardDragTokenVisualFactory.cs"));
        var start = source.IndexOf("private static async Task ApplySoftwareBitmapAsync(", StringComparison.Ordinal);
        var end = source.IndexOf("private static byte[] GenerateFallbackCirclePixels(", start, StringComparison.Ordinal);
        var operation = source[start..end];

        Assert.Contains("using var memStream = new InMemoryRandomAccessStream();", operation, StringComparison.Ordinal);
        var flush = operation.IndexOf("await encoder.FlushAsync();", StringComparison.Ordinal);
        var decode = operation.IndexOf("await decoder.GetSoftwareBitmapAsync();", StringComparison.Ordinal);
        var publish = operation.IndexOf("args.DragUI.SetContentFromSoftwareBitmap(", StringComparison.Ordinal);
        Assert.True(flush >= 0 && decode > flush && publish > decode);
        Assert.DoesNotContain("memStream.Dispose()", operation, StringComparison.Ordinal);
    }
}
