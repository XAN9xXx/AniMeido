using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using System.Diagnostics;
using System.Reflection;
using Xunit;
using Xunit.Abstractions;
using static AniMeido.Desktop.Tests.DesktopTodayRecommendationPageTests;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopImageMemoryTests(ITestOutputHelper output)
{
    [Fact]
    public Task UnloadingOneImageKeepsTheOtherVisibleCopyOfTheSameFile()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var directory = Directory.CreateTempSubdirectory("AniMeido-shared-image-");
            var path = CreateImage(directory.FullName, "shared.jpg", SKColors.DarkBlue);
            var first = new Image { Width = 100, Height = 150 };
            var second = new Image { Width = 100, Height = 150 };
            try
            {
                root.Children.Add(first); root.Children.Add(second); root.UpdateLayout();
                ManagedImageLoader.ConfigureLocal(first, path, 100);
                ManagedImageLoader.ConfigureLocal(second, path, 100);
                var a = await LoadedBitmapAsync(first);
                var b = await LoadedBitmapAsync(second);
                Assert.NotSame(a, b);
                Assert.Same(Surface(a), Surface(b));
                root.Children.Remove(first);
                await Task.Delay(1500);
                Assert.Equal(0, Subscribers(a));
                Assert.Same(b, second.Source);
                Assert.True(DecodedBytes(b) > 0);
                Assert.Equal(100, b.PixelWidth);
            }
            finally { root.Children.Remove(first); root.Children.Remove(second); ManagedImageLoader.Cancel(first); ManagedImageLoader.Cancel(second); directory.Delete(true); }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ReuseDuringDecodeKeepsTheNewImageAfterTheOldReply(bool leaveAndReturn)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var directory = Directory.CreateTempSubdirectory("AniMeido-image-reuse-");
            var first = CreateImage(directory.FullName, "first.jpg", SKColors.DarkBlue);
            var second = CreateImage(directory.FullName, "second.jpg", SKColors.DarkRed);
            var image = new Image { Width = 100, Height = 150 };
            try
            {
                root.Children.Add(image); root.UpdateLayout();
                ManagedImageLoader.ConfigureLocal(image, first, 100);
                var old = Assert.IsType<BitmapImage>(image.Source);
                if (leaveAndReturn) { root.Children.Remove(image); root.Children.Add(image); root.UpdateLayout(); }
                ManagedImageLoader.ConfigureLocal(image, second, 100);
                var current = await LoadedBitmapAsync(image);
                await Task.Delay(50);
                Assert.NotSame(old, current);
                Assert.Same(current, image.Source);
                Assert.Equal(new Uri(second), Assert.IsType<BitmapImage>(current).UriSource);
                Assert.Equal(100, current.PixelWidth);
            }
            finally { root.Children.Remove(image); ManagedImageLoader.Cancel(image); directory.Delete(true); }
        });

    private static string CreateImage(string directory, string name, SKColor color)
    {
        var path = Path.Combine(directory, name);
        using var bitmap = new SKBitmap(1200, 1800);
        bitmap.Erase(color);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, 90);
        using var file = File.Create(path);
        encoded.SaveTo(file);
        return path;
    }

    private static async Task<BitmapSource> LoadedBitmapAsync(Image image)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((image.Source as BitmapSource)?.PixelWidth is not > 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        var source = Assert.IsAssignableFrom<BitmapSource>(image.Source);
        Assert.True(source.PixelWidth > 0);
        return source;
    }

    [Fact]
    public Task LeavingGalleryDropsTheDecodedDetailPreviewAndReopeningLoadsItAgain()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var path = CreateImage(Path.GetDirectoryName(fixture.DatabasePath)!, "gallery-preview.jpg", SKColors.Navy);
            var store = new ArchiveService(fixture.Factory);
            await store.ImportScreenshotsAsync([new AnimeScreenshot("preview", path, "", DateTimeOffset.UnixEpoch,
                "", "", 1200, 1800, null, null, null, null, "", true)]);
            var page = fixture.Archives(new Source());
            await page.OnNavigatedToAsync("preview");
            try
            {
                await AttachAsync(root, page); await page.PendingActivation; await page.PendingRead;
                var image = Assert.IsType<Image>(page.FindName("ScreenshotDetailImage"));
                await UnoVisualEvidence.WaitForAsync(() => image.Source is BitmapSource decoded && Surface(decoded) is not null);
                var surface = Surface((BitmapSource)image.Source)!;
                var before = SurfaceBytes(surface);
                Assert.True(before > 0);
                await DetachAsync(root, page);
                Assert.Null(image.Source);
                await UnoVisualEvidence.WaitForAsync(() => SurfaceBytes(surface) == 0);
                output.WriteLine($"gallery-detail decodedBytes={before}; afterLeave={SurfaceBytes(surface)}");
                await page.OnNavigatedToAsync("preview");
                await AttachAsync(root, page); await page.PendingActivation; await page.PendingRead;
                await UnoVisualEvidence.WaitForAsync(() => image.Source is BitmapSource reopened && DecodedBytes(reopened) > 0);
            }
            finally { await DetachAsync(root, page); await page.PendingRead; }
        });

    [Fact]
    public Task ArchiveStripDecodesAtTheViewportWidthOnLoadResizeAndReentry()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var archive = fixture.Archives(new Source());
            var directory = Directory.CreateTempSubdirectory("AniMeido-archive-strip-");
            await AttachAsync(root, archive);
            var item = Assert.IsType<Border>(((ItemsControl)archive.FindName("ArchiveScreenshotStrip")).ItemTemplate.LoadContent());
            var image = Assert.IsType<Image>(item.Child);
            using var trace = new DesktopManagedImageLifetimeTests.ImageReloadTrace(image);
            try
            {
                var path = CreateImage(directory.FullName, "portrait.jpg", SKColors.DarkBlue);
                item.DataContext = new AnimeScreenshot("test", path, "", DateTimeOffset.UnixEpoch,
                    "", "", 1200, 1800, null, null, null, null, "", true);
                root.Children.Add(item);
                await CheckWidthAsync(180);
                item.Width = 250;
                await CheckWidthAsync(250);
                root.Children.Remove(item);
                root.UpdateLayout();
                Assert.Null(image.Source);
                root.Children.Add(item);
                await CheckWidthAsync(250);
            }
            finally
            {
                root.Children.Remove(item);
                await DetachAsync(root, archive);
                directory.Delete(true);
            }

            async Task CheckWidthAsync(int expected)
            {
                for (var i = 0; i < 30; i++) { root.UpdateLayout(); await Task.Delay(16); }
                Assert.Equal(expected, Assert.IsType<BitmapImage>(image.Source).PixelWidth);
                Assert.Equal(new[] { expected }, trace.Widths);
                trace.Widths.Clear();
            }
        });

    [Fact]
    [Trait("Category", "PerformanceDiagnostic")]
    public Task RepeatedArchiveGalleryVisitsReleaseImageOwnersAndBoundDecodedSurfaces()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var archive = fixture.Archives(new Source());
            await AttachAsync(root, archive);
            var template = ((ItemsControl)archive.FindName("ReviewScreenshots")).ItemTemplate;
            var directory = Directory.CreateTempSubdirectory("AniMeido-gallery-memory-");
            using var pixels = new SKBitmap(1200, 1800);
            pixels.Erase(SKColors.DarkBlue);
            using var encoded = pixels.Encode(SKEncodedImageFormat.Jpeg, 90);
            var unloaded = new List<Image>();
            var sources = new List<WeakReference<BitmapSource>>();
            var surfaces = new List<WeakReference<object>>();
            Sample("empty");
            try
            {
                for (var visit = 0; visit < 10; visit++)
                {
                    var gallery = new StackPanel { Orientation = Orientation.Horizontal };
                    var thumbnails = new List<Image>();
                    for (var index = 0; index < 20; index++)
                    {
                        var path = Path.Combine(directory.FullName, $"{visit}-{index}.jpg");
                        using (var file = File.Create(path)) encoded.SaveTo(file);
                        // 使用档案截图真实模板；裸 Image 没有页面负责取消加载的 Unloaded 处理。
                        var item = Assert.IsAssignableFrom<FrameworkElement>(template.LoadContent());
                        var image = Assert.Single(UnoVisualEvidence.Descendants(item).OfType<Image>());
                        image.Width = 300;
                        image.Height = 450;
                        item.DataContext = new AnimeScreenshot($"{visit}-{index}", path, "", DateTimeOffset.UnixEpoch,
                            "", "", 1200, 1800, null, null, null, null, "", true);
                        thumbnails.Add(image);
                        gallery.Children.Add(item);
                    }
                    root.Children.Add(gallery);
                    root.UpdateLayout();
                    var deadline = DateTime.UtcNow.AddSeconds(5);
                    while (thumbnails.Any(image => (image.Source as BitmapSource)?.PixelWidth != 300)
                        && DateTime.UtcNow < deadline) await Task.Delay(10);
                    Assert.All(thumbnails, image => Assert.Equal(300, Assert.IsAssignableFrom<BitmapSource>(image.Source).PixelWidth));
                    sources.AddRange(thumbnails.Select(image => new WeakReference<BitmapSource>((BitmapSource)image.Source)));
                    surfaces.AddRange(thumbnails.Select(image => new WeakReference<object>(Surface((BitmapSource)image.Source)!)));
                    root.Children.Remove(gallery);
                    unloaded.AddRange(thumbnails);
                    await Task.Delay(300);
                    Sample($"unloaded-{visit + 1}");
                }
                await Task.Delay(1500);
                root.InvalidateMeasure();
                root.UpdateLayout();
                Sample("idle");
                var decodedBytes = sources.Sum(reference => reference.TryGetTarget(out var source) ? DecodedBytes(source) : 0);
                var unique = new HashSet<object>(ReferenceEqualityComparer.Instance);
                foreach (var reference in surfaces)
                    if (reference.TryGetTarget(out var surface) && SurfaceBytes(surface) > 0) unique.Add(surface);
                var uniqueBytes = unique.Sum(SurfaceBytes);
                output.WriteLine($"visibleImages=0; unloadedImages={unloaded.Count}; retainedSources={unloaded.Count(image => image.Source is not null)}; sourceDecodedBytes={decodedBytes}; uniqueSurfaces={unique.Count}; uniqueDecodedBytes={uniqueBytes}");
                Assert.All(unloaded, image => Assert.Null(image.Source));
                Assert.Empty(unique);
                Assert.Equal(0L, decodedBytes);
                foreach (var reference in sources)
                    if (reference.TryGetTarget(out var source)) Assert.Equal(0, Subscribers(source));
            }
            finally
            {
                foreach (var image in unloaded) ManagedImageLoader.Cancel(image);
                await DetachAsync(root, archive);
                directory.Delete(true);
            }
        });

    private void Sample(string name)
    {
        using var process = Process.GetCurrentProcess(); process.Refresh();
        output.WriteLine($"{name}: workingMiB={process.WorkingSet64 / 1048576d:F1}; privateMiB={process.PrivateMemorySize64 / 1048576d:F1}; heapMiB={GC.GetTotalMemory(false) / 1048576d:F1}; skiaResourceMiB={SKGraphics.GetResourceCacheTotalBytesUsed() / 1048576d:F1}; fontMiB={SKGraphics.GetFontCacheUsed() / 1048576d:F1}");
    }

    [Fact]
    public Task LargeLocalImagesDecodeToTheirDisplaySizeAndUnloadEndsTheSubscription()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var directory = Directory.CreateTempSubdirectory("AniMeido-image-memory-");
            var path = Path.Combine(directory.FullName, "large.jpg");
            using (var pixels = new SKBitmap(1200, 1800))
            {
                pixels.Erase(SKColors.DarkBlue);
                using var encoded = pixels.Encode(SKEncodedImageFormat.Jpeg, 90);
                using var file = File.Create(path);
                encoded.SaveTo(file);
            }
            var image = new Image { Width = 100, Height = 150 };
            try
            {
                root.Children.Add(image);
                root.UpdateLayout();
                ManagedImageLoader.ConfigureLocal(image, path, 100);
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while ((image.Source as BitmapSource)?.PixelWidth is not > 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
                var bitmap = Assert.IsAssignableFrom<BitmapSource>(image.Source);
                using var process = Process.GetCurrentProcess(); process.Refresh();
                output.WriteLine($"decoded={bitmap.PixelWidth}x{bitmap.PixelHeight}; workingMiB={process.WorkingSet64 / 1048576d:F1}; privateMiB={process.PrivateMemorySize64 / 1048576d:F1}; heapMiB={GC.GetTotalMemory(false) / 1048576d:F1}; skiaResourceMiB={SKGraphics.GetResourceCacheTotalBytesUsed() / 1048576d:F1}; fontMiB={SKGraphics.GetFontCacheUsed() / 1048576d:F1}");
                Assert.InRange(bitmap.PixelWidth, 1, 100);
                Assert.InRange(bitmap.PixelHeight, 1, 150);
                Assert.True(DecodedBytes(bitmap, trace: true) > 0);
                root.Children.Remove(image);
                await Task.Delay(1500);
                root.InvalidateMeasure();
                root.UpdateLayout();
                Assert.Equal(0, Subscribers(bitmap));
                root.Children.Add(image);
                root.UpdateLayout();
                deadline = DateTime.UtcNow.AddSeconds(5);
                while ((image.Source as BitmapSource)?.PixelWidth is not > 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
                Assert.Equal(100, Assert.IsAssignableFrom<BitmapSource>(image.Source).PixelWidth);
            }
            finally { root.Children.Remove(image); ManagedImageLoader.Cancel(image); directory.Delete(true); }
        });

    // Uno 的 PixelWidth 会保留占位尺寸；检查实际解码画面，避免把 Source 引用误当作像素保留。
    private long DecodedBytes(BitmapSource source, bool trace = false)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var data = typeof(Microsoft.UI.Xaml.Media.ImageSource).GetField("_imageData", flags)!.GetValue(source)!;
        var surface = data.GetType().GetProperty("CompositionSurface", flags)!.GetValue(data);
        if (trace)
        {
            var subscriptions = typeof(Microsoft.UI.Xaml.Media.ImageSource).GetField("_subscriptions", flags)!.GetValue(source)!;
            var pending = typeof(Microsoft.UI.Xaml.Media.ImageSource).GetField("_pendingRelease", flags)!.GetValue(source);
            output.WriteLine($"shared={data.GetType().GetProperty("IsShared", flags)!.GetValue(data)}; subscriptions={subscriptions.GetType().GetProperty("Count", flags)!.GetValue(subscriptions)}; pendingRelease={pending is not null}");
        }
        return surface is null ? 0 : (long)surface.GetType().GetProperty("DecodedByteCount", flags)!.GetValue(surface)!;
    }

    private static object? Surface(BitmapSource source)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var data = typeof(Microsoft.UI.Xaml.Media.ImageSource).GetField("_imageData", flags)!.GetValue(source)!;
        return data.GetType().GetProperty("CompositionSurface", flags)!.GetValue(data);
    }

    private static long SurfaceBytes(object surface)
        => (long)surface.GetType().GetProperty("DecodedByteCount", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(surface)!;

    private static int Subscribers(BitmapSource source)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var subscriptions = typeof(Microsoft.UI.Xaml.Media.ImageSource).GetField("_subscriptions", flags)!.GetValue(source)!;
        return (int)subscriptions.GetType().GetProperty("Count", flags)!.GetValue(subscriptions)!;
    }
}
