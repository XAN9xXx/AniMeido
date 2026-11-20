using AniMeido.Plugin.Base.Services;
using Microsoft.UI.Xaml;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using System.Diagnostics;
using System.Reflection;
using Xunit;
using Xunit.Abstractions;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
[Trait("Category", "Mechanism")]
public sealed class DesktopManagedImageLifetimeTests(ITestOutputHelper output)
{
    private static int _nextId = 1_000_000;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CachedItemEnteringTheViewportShowsItsImageWithoutAnIntermediatePlaceholder(bool avatar)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            using var first = new CachedImage(avatar, 96, 144);
            using var next = new CachedImage(avatar, 96, 144);
            var image = NewImage();
            var placeholders = 0;
            var token = image.RegisterPropertyChangedCallback(Image.SourceProperty, (_, _) =>
            {
                if (image.Source is BitmapImage bitmap && bitmap.UriSource == ImageCacheHelper.PlaceholderUri)
                    placeholders++;
            });
            try
            {
                first.Configure(image);
                root.Children.Add(image);
                await ReadyAsync(image);
                root.Children.Remove(image);
                next.Configure(image);
                root.Children.Add(image);
                await ReadyAsync(image);
                Assert.Equal(new Uri(next.Path), Assert.IsType<BitmapImage>(image.Source).UriSource);
                Assert.Equal(0, placeholders);
            }
            finally
            {
                image.UnregisterPropertyChangedCallback(Image.SourceProperty, token);
                root.Children.Remove(image);
                ManagedImageLoader.Cancel(image);
            }
        });

    [Fact]
    public Task BindingACachedCoverBeforeMountingDoesNotStartTheDownloadIndicator()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            using var entry = new CachedImage(false, 96, 144);
            var states = new List<ManagedImageLoadState>();
            var image = NewImage();
            ManagedImageLoader.ConfigureCover(image, entry.Id, entry.Url, image.Width, states.Add);
            root.Children.Add(image);
            try
            {
                await ReadyAsync(image);
                Assert.Contains(ManagedImageLoadState.Loaded, states);
                Assert.DoesNotContain(ManagedImageLoadState.Loading, states);
            }
            finally
            {
                root.Children.Remove(image);
                ManagedImageLoader.Cancel(image);
            }
        });

    [Fact]
    public Task MissingCoverUsesTheRequestedDecodeWidth()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var image = new Image { Width = 120 };
            ManagedImageLoader.ConfigureCover(image, -1, null, 120);
            root.Children.Add(image);
            try
            {
                var bitmap = Assert.IsType<BitmapImage>(image.Source);
                Assert.Equal(120, bitmap.DecodePixelWidth);
                var deadline = Stopwatch.StartNew();
                while (bitmap.PixelWidth == 0 && deadline.Elapsed < TimeSpan.FromSeconds(5))
                    await Task.Delay(16);
                Assert.Equal(120, bitmap.PixelWidth);
            }
            finally { root.Children.Remove(image); }
        });

    [Theory]
    [InlineData(600, 600)]
    [InlineData(900, 600)]
    [InlineData(600, 900)]
    public Task ActualCardKeepsOneDecodeSizeAfterLayoutSettles(int pixelWidth, int pixelHeight)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            using var entry = new CachedImage(avatar: false, pixelWidth, pixelHeight);
            var card = new AnimeCard
            {
                Anime = new Anime(entry.Id, "真实卡片尺寸回归", null, [], new DateOnly(2026, 1, 1), entry.Url, "简介", 2026, 1),
                ShowQuickActions = true,
            };
            var image = (Image)card.FindName("CoverImage");
            using var trace = new ImageReloadTrace(image);
            try
            {
                using var work = new ImageWorkSample();
                root.Children.Add(card);
                for (var frame = 0; frame < 100; frame++)
                {
                    if (frame is 35 or 55)
                    {
                        typeof(AnimeCard).GetField("_pointerOver", Flags)!.SetValue(card, frame == 35);
                        typeof(AnimeCard).GetMethod("RefreshInteraction", Flags)!.Invoke(card, null);
                    }
                    root.UpdateLayout();
                    await Task.Delay(16);
                }
                output.WriteLine($"image={pixelWidth}x{pixelHeight}; {trace}; {work}");
                Assert.Equal(1, trace.Loaded);
                Assert.Equal(0, trace.Unloaded);
                Assert.InRange(trace.Widths.Count, 1, 3);
                Assert.True(Bytes(Surface(image)) > 0);
                card.CardWidth = 190;
                for (var frame = 0; frame < 30; frame++) { root.UpdateLayout(); await Task.Delay(16); }
                Assert.Equal(190, trace.Widths[^1]);
                var settled = trace.Widths.Count;
                using var idle = new ImageWorkSample();
                for (var frame = 0; frame < 40; frame++) { root.UpdateLayout(); await Task.Delay(16); }
                output.WriteLine($"afterResizeSources={trace.Widths.Count}; finalWidth={trace.Widths[^1]}; idleSourceChanges={trace.Widths.Count - settled}; idle: {idle}");
                Assert.Equal(settled, trace.Widths.Count);
            }
            finally
            {
                root.Children.Remove(card);
                ManagedImageLoader.Cancel(image);
                await Task.Delay(1500);
            }
        });

    [Theory]
    [InlineData(false, 100)]
    [InlineData(false, 180)]
    [InlineData(true, 100)]
    [InlineData(true, 180)]
    public Task UnloadingOneCopyKeepsTheOtherCopyVisible(bool avatar, int secondWidth)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            using var entry = new CachedImage(avatar);
            var first = NewImage();
            var second = NewImage(secondWidth);
            try
            {
                root.Children.Add(first);
                root.Children.Add(second);
                root.UpdateLayout();
                entry.Configure(first);
                entry.Configure(second);
                await ReadyAsync(first);
                await ReadyAsync(second);
                var visibleSource = second.Source;
                root.Children.Remove(first);
                await SettleAsync(root);
                Assert.Same(visibleSource, second.Source);
                Assert.True(Bytes(Surface(second)) > 0);
                Assert.Equal(secondWidth, Assert.IsAssignableFrom<BitmapSource>(second.Source).PixelWidth);
                Assert.Equal(0, Subscriptions(first));
            }
            finally { ManagedImageLoader.Cancel(first); ManagedImageLoader.Cancel(second); }
        });

    [Fact]
    public Task ActualCardReconfiguresOnceWhenTheRootDpiChangesAndThenSettles()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            using var entry = new CachedImage(false, 900, 600);
            var card = new AnimeCard
            {
                Anime = new Anime(entry.Id, "DPI 变化", null, [], new DateOnly(2026, 1, 1), entry.Url, "简介", 2026, 1),
                CardWidth = 150,
            };
            var image = (Image)card.FindName("CoverImage");
            var xamlRoot = root.XamlRoot!;
            using var trace = new ImageReloadTrace(image);
            try
            {
                root.Children.Add(card);
                root.UpdateLayout();
                await ReadyAsync(image);
                SetRootScale(xamlRoot, 2);
                for (var frame = 0; frame < 40; frame++) { root.UpdateLayout(); await Task.Delay(16); }
                output.WriteLine($"scale={xamlRoot.RasterizationScale}; {trace}");
                Assert.Equal(2, xamlRoot.RasterizationScale);
                Assert.Equal(300, Assert.IsType<BitmapImage>(image.Source).DecodePixelWidth);
                var settled = trace.Widths.Count;
                for (var frame = 0; frame < 40; frame++) { root.UpdateLayout(); await Task.Delay(16); }
                Assert.Equal(settled, trace.Widths.Count);
                SetRootScale(xamlRoot, 0);
                for (var frame = 0; frame < 40; frame++) { root.UpdateLayout(); await Task.Delay(16); }
                Assert.Equal(150, Assert.IsType<BitmapImage>(image.Source).DecodePixelWidth);
                output.WriteLine($"restoredScale={xamlRoot.RasterizationScale}; {trace}");
            }
            finally
            {
                root.Children.Remove(card);
                ManagedImageLoader.Cancel(image);
                SetRootScale(xamlRoot, 0);
                root.UpdateLayout();
                await Task.Delay(1500);
            }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ExplicitViewportWidthChangesReloadEvenWhenTheFilledImageWidthStaysTheSame(bool avatar)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            using var entry = new CachedImage(avatar, 600, 600);
            var image = new Image { Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill };
            var viewport = new Border { Width = 100, Height = 225, Child = image };
            using var trace = new ImageReloadTrace(image);
            try
            {
                root.Children.Add(viewport);
                root.UpdateLayout();
                if (avatar) ManagedImageLoader.ConfigureAvatar(image, entry.Url, 200);
                else ManagedImageLoader.ConfigureCover(image, entry.Id, entry.Url, 200);
                await ReadyAsync(image);
                for (var frame = 0; frame < 10; frame++) { root.UpdateLayout(); await Task.Delay(16); }
                Assert.Equal(100, Assert.IsType<BitmapImage>(image.Source).DecodePixelWidth);
                var drawnWidth = image.ActualWidth;
                viewport.Width = 140;
                for (var frame = 0; frame < 40; frame++) { root.UpdateLayout(); await Task.Delay(16); }
                output.WriteLine($"avatar={avatar}; drawnWidth={drawnWidth}->{image.ActualWidth}; {trace}");
                Assert.Equal(drawnWidth, image.ActualWidth);
                Assert.Equal(140, Assert.IsType<BitmapImage>(image.Source).DecodePixelWidth);
                Assert.Equal(2, trace.Widths.Count);
            }
            finally
            {
                root.Children.Remove(viewport);
                ManagedImageLoader.Cancel(image);
                await Task.Delay(1500);
            }
        });

    [Fact]
    public Task RootSubscriptionsFollowFastReentryAndRootMovesWithoutRetainingAnUnloadedImage()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            using var entry = new CachedImage(false, 900, 600);
            var image = NewImage(150);
            var firstRoot = root.XamlRoot!;
            var otherContent = new Grid();
            var otherWindow = new Window { Content = otherContent };
            otherWindow.Activate();
            using var trace = new ImageReloadTrace(image);
            try
            {
                root.Children.Add(image);
                root.UpdateLayout();
                entry.Configure(image);
                await ReadyAsync(image);
                Assert.Equal(1, RootSubscriptions(firstRoot, image));
                SetRootScale(firstRoot, 2);
                // 同步发出根通知，使重配排进队列；随后先离页、重入，再搬到另一个根。
                typeof(XamlRoot).GetMethod("RaiseChangedEvent", Flags)!.Invoke(firstRoot, null);
                root.Children.Remove(image);
                Assert.Equal(0, RootSubscriptions(firstRoot, image));
                root.Children.Add(image);
                root.UpdateLayout();
                await UntilAsync(() => RootSubscriptions(firstRoot, image) == 1);
                root.Children.Remove(image);
                // Uno 保留离树元素的根；按公开 API 在重新挂载前指定目标窗口的根。
                image.XamlRoot = otherContent.XamlRoot;
                otherContent.Children.Add(image);
                otherContent.UpdateLayout();
                var secondRoot = otherContent.XamlRoot!;
                try { await UntilAsync(() => RootSubscriptions(secondRoot, image) == 1); }
                finally
                {
                    output.WriteLine($"move: imageLoaded={image.IsLoaded}; oldRoot={ReferenceEquals(image.XamlRoot, firstRoot)}; newRoot={ReferenceEquals(image.XamlRoot, secondRoot)}; oldSubscriptions={RootSubscriptions(firstRoot, image)}; newSubscriptions={RootSubscriptions(secondRoot, image)}; {trace}");
                }
                await ReadyAsync(image);
                Assert.NotSame(firstRoot, secondRoot);
                Assert.Equal(0, RootSubscriptions(firstRoot, image));
                Assert.Equal(1, RootSubscriptions(secondRoot, image));
                SetRootScale(firstRoot, 0);
                for (var frame = 0; frame < 30; frame++) { otherContent.UpdateLayout(); await Task.Delay(16); }
                Assert.Equal(150, Assert.IsType<BitmapImage>(image.Source).DecodePixelWidth);
                var settled = trace.Widths.Count;
                otherContent.Children.Remove(image);
                Assert.Equal(0, RootSubscriptions(secondRoot, image));
                SetRootScale(secondRoot, 2);
                typeof(XamlRoot).GetMethod("RaiseChangedEvent", Flags)!.Invoke(secondRoot, null);
                await Task.Delay(50);
                Assert.Equal(settled, trace.Widths.Count);
                SetRootScale(secondRoot, 0);
                otherContent.Children.Add(image);
                otherContent.UpdateLayout();
                ManagedImageLoader.Cancel(image);
                Assert.Equal(0, RootSubscriptions(secondRoot, image));
                output.WriteLine(trace.ToString());
            }
            finally
            {
                root.Children.Remove(image);
                otherContent.Children.Remove(image);
                ManagedImageLoader.Cancel(image);
                SetRootScale(firstRoot, 0);
                if (otherContent.XamlRoot is { } secondRoot) SetRootScale(secondRoot, 0);
                otherWindow.Close();
                await Task.Delay(1500);
            }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task OffscreenPixelsReleaseAndReturnWithTheSameRequest(bool avatar)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            using var entry = new CachedImage(avatar);
            var image = NewImage();
            try
            {
                root.Children.Add(image);
                root.UpdateLayout();
                entry.Configure(image);
                await ReadyAsync(image);
                var oldSurface = Surface(image);
                root.Children.Remove(image);
                await SettleAsync(root);
                await UntilAsync(() => Bytes(oldSurface) == 0, "离屏画面应在 5 秒内释放。");
                output.WriteLine($"offscreenBytes={Bytes(oldSurface)}; subscriptions={Subscriptions(image)}");
                Assert.Equal(0, Subscriptions(image));
                Assert.Equal(0, Bytes(oldSurface));
                root.Children.Add(image);
                root.UpdateLayout();
                await ReadyAsync(image);
                Assert.Equal(new Uri(entry.Path), Assert.IsType<BitmapImage>(image.Source).UriSource);
                Assert.True(Bytes(Surface(image)) > 0);
            }
            finally { ManagedImageLoader.Cancel(image); }
        });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task ReconfigureDuringDecodeKeepsTheLatestRequest(bool avatar, bool leaveAndReturn)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            using var oldEntry = new CachedImage(avatar);
            using var newEntry = new CachedImage(avatar);
            var image = NewImage();
            try
            {
                root.Children.Add(image);
                root.UpdateLayout();
                oldEntry.Configure(image);
                var oldSource = image.Source;
                if (leaveAndReturn) { root.Children.Remove(image); root.Children.Add(image); root.UpdateLayout(); }
                newEntry.Configure(image);
                await ReadyAsync(image);
                await Task.Delay(100);
                Assert.NotSame(oldSource, image.Source);
                Assert.Equal(new Uri(newEntry.Path), Assert.IsType<BitmapImage>(image.Source).UriSource);
                Assert.True(Bytes(Surface(image)) > 0);
            }
            finally { ManagedImageLoader.Cancel(image); }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ResizeUsesTheLatestDisplayWidth(bool avatar)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            using var entry = new CachedImage(avatar);
            var image = NewImage();
            try
            {
                root.Children.Add(image);
                root.UpdateLayout();
                entry.Configure(image);
                await ReadyAsync(image);
                var first = image.Source;
                image.Width = 180;
                image.Height = 270;
                image.InvalidateMeasure();
                root.InvalidateMeasure();
                root.UpdateLayout();
                try
                {
                    await UntilAsync(() => !ReferenceEquals(first, image.Source)
                        && image.Source is BitmapSource { PixelWidth: 180 } && Bytes(Surface(image)) > 0);
                }
                finally
                {
                    output.WriteLine($"width={image.Width}; actualWidth={image.ActualWidth}; decodeWidth={(image.Source as BitmapImage)?.DecodePixelWidth}; pixelWidth={(image.Source as BitmapSource)?.PixelWidth}; sameSource={ReferenceEquals(first, image.Source)}");
                }
                Assert.Equal(180, Assert.IsAssignableFrom<BitmapSource>(image.Source).PixelWidth);
            }
            finally { ManagedImageLoader.Cancel(image); }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task FailedCachedDecodeRetriesWithTheReplacementDiskEntry(bool avatar)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            using var entry = new CachedImage(avatar);
            File.WriteAllText(entry.Path, "invalid image");
            var image = NewImage();
            var replaced = false;
            // 自动重试清除损坏文件后，模拟合并下载已交付新文件；测试全程使用隔离磁盘缓存。
            var callback = image.RegisterPropertyChangedCallback(Image.SourceProperty, (_, _) =>
            {
                if (!replaced && image.Source is BitmapImage bitmap && bitmap.UriSource == ImageCacheHelper.PlaceholderUri)
                {
                    replaced = true;
                    entry.WriteImage();
                }
            });
            try
            {
                root.Children.Add(image);
                root.UpdateLayout();
                entry.Configure(image);
                await ReadyAsync(image);
                Assert.True(replaced);
                Assert.Equal(new Uri(entry.Path), Assert.IsType<BitmapImage>(image.Source).UriSource);
            }
            finally
            {
                image.UnregisterPropertyChangedCallback(Image.SourceProperty, callback);
                ManagedImageLoader.Cancel(image);
            }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ExplicitRetryRecoversAfterTheAutomaticRetryFails(bool avatar)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            using var entry = new CachedImage(avatar);
            File.WriteAllText(entry.Path, "invalid image");
            var image = NewImage();
            var placeholders = 0;
            var replacementReady = false;
            var callback = image.RegisterPropertyChangedCallback(Image.SourceProperty, (_, _) =>
            {
                if (image.Source is BitmapImage bitmap && bitmap.UriSource == ImageCacheHelper.PlaceholderUri)
                {
                    placeholders++;
                    if (replacementReady) entry.WriteImage();
                }
            });
            try
            {
                root.Children.Add(image);
                root.UpdateLayout();
                entry.Configure(image);
                // 损坏缓存触发一次自动重试；被禁止的测试域名使下载立即失败，无外网请求。
                await UntilAsync(() => placeholders >= 2 && !File.Exists(entry.Path));
                replacementReady = true;
                ManagedImageLoader.Retry(image);
                await ReadyAsync(image);
                Assert.Equal(new Uri(entry.Path), Assert.IsType<BitmapImage>(image.Source).UriSource);
            }
            finally
            {
                image.UnregisterPropertyChangedCallback(Image.SourceProperty, callback);
                ManagedImageLoader.Cancel(image);
            }
        });

    [Fact]
    [Trait("Category", "PerformanceDiagnostic")]
    public Task RepeatedCoverVisitsReleasePixelsAndMeasureRevisitCost() => MeasureGalleryAsync(avatar: false);

    [Fact]
    [Trait("Category", "PerformanceDiagnostic")]
    public Task RepeatedAvatarVisitsReleasePixelsAndMeasureRevisitCost() => MeasureGalleryAsync(avatar: true);

    private Task MeasureGalleryAsync(bool avatar)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var entries = Enumerable.Range(0, 200).Select(_ => new CachedImage(avatar)).ToArray();
            var pages = new List<StackPanel>();
            var surfaces = new List<WeakReference<object>>();
            var initialReady = new List<double>();
            using var memory = new MemorySample();
            var allocated = GC.GetTotalAllocatedBytes(precise: false);
            var elapsed = Stopwatch.StartNew();
            try
            {
                for (var visit = 0; visit < 10; visit++)
                {
                    var page = new StackPanel { Orientation = Orientation.Horizontal };
                    pages.Add(page);
                    for (var i = 0; i < 20; i++) page.Children.Add(NewImage(300));
                    var load = Stopwatch.StartNew();
                    root.Children.Add(page);
                    root.UpdateLayout();
                    for (var i = 0; i < 20; i++) entries[visit * 20 + i].Configure((Image)page.Children[i]);
                    await UntilAsync(() => page.Children.Cast<Image>().All(image => Bytes(Surface(image)) > 0));
                    initialReady.Add(load.Elapsed.TotalMilliseconds);
                    Observe(page, surfaces);
                    root.Children.Remove(page);
                    await SettleAsync(root);
                }
                var idle = memory.Current();
                var retained = UniqueBytes(surfaces);
                var beforeRevisit = surfaces.Count;
                var revisit = Stopwatch.StartNew();
                root.Children.Add(pages[0]);
                root.UpdateLayout();
                await UntilAsync(() => pages[0].Children.Cast<Image>().All(image => Bytes(Surface(image)) > 0));
                var revisitMs = revisit.Elapsed.TotalMilliseconds;
                Observe(pages[0], surfaces);
                var revisitDecodes = surfaces.Count - beforeRevisit;
                root.Children.Remove(pages[0]);
                await SettleAsync(root);
                var afterRevisit = UniqueBytes(surfaces);
                var final = memory.Current();
                var allocation = GC.GetTotalAllocatedBytes(precise: false) - allocated;
                output.WriteLine($"kind={(avatar ? "Avatar" : "Cover")}; initialDecodedSurfaces={beforeRevisit}; revisitNewSurfaces={revisitDecodes}; initialReadyMs=[{string.Join(",", initialReady.Select(ms => ms.ToString("F1")))}]; revisitReadyMs={revisitMs:F1}; elapsedMs={elapsed.Elapsed.TotalMilliseconds:F1}");
                output.WriteLine($"idleUniqueSurfaces={retained.Count}; idleDecodedBytes={retained.Bytes}; afterRevisitUniqueSurfaces={afterRevisit.Count}; afterRevisitDecodedBytes={afterRevisit.Bytes}; allocatedBytes={allocation}");
                output.WriteLine($"startPrivateMiB={memory.StartPrivate:F2}; peakPrivateMiB={memory.PeakPrivate:F2}; idlePrivateMiB={idle.Private:F2}; finalPrivateMiB={final.Private:F2}; peakWorkingMiB={memory.PeakWorking:F2}; finalWorkingMiB={final.Working:F2}");
                Assert.All(pages.SelectMany(page => page.Children.Cast<Image>()), image => Assert.Equal(0, Subscriptions(image)));
                Assert.Equal(0, retained.Bytes);
                Assert.Equal(0, afterRevisit.Bytes);
            }
            finally
            {
                root.Children.Clear();
                foreach (var image in pages.SelectMany(page => page.Children.Cast<Image>())) ManagedImageLoader.Cancel(image);
                foreach (var entry in entries) entry.Dispose();
            }
        });

    private static Image NewImage(double width = 100) => new() { Width = width, Height = width * 1.5 };
    private static Task ReadyAsync(Image image) => UntilAsync(() => image.Source is BitmapImage bitmap
        && bitmap.UriSource != ImageCacheHelper.PlaceholderUri && Bytes(Surface(image)) > 0);
    private static async Task UntilAsync(Func<bool> condition, string failure = "图片应在 5 秒内完成解码。")
    {
        var timeout = Stopwatch.StartNew();
        while (!condition() && timeout.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
        Assert.True(condition(), failure);
    }

    private static async Task SettleAsync(Grid root)
    {
        await Task.Delay(1500);
        root.InvalidateMeasure();
        root.UpdateLayout();
        await Task.Delay(50);
    }

    private static void Observe(StackPanel page, List<WeakReference<object>> surfaces)
    {
        foreach (var image in page.Children.Cast<Image>())
            if (Surface(image) is { } surface && !surfaces.Any(reference => reference.TryGetTarget(out var known) && ReferenceEquals(known, surface)))
                surfaces.Add(new(surface));
    }

    private static (int Count, long Bytes) UniqueBytes(List<WeakReference<object>> surfaces)
    {
        var unique = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var reference in surfaces)
            if (reference.TryGetTarget(out var surface) && Bytes(surface) > 0) unique.Add(surface);
        return (unique.Count, unique.Sum(Bytes));
    }

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static void SetRootScale(XamlRoot root, float value)
    {
        var tree = typeof(XamlRoot).GetProperty("VisualTree", Flags)!.GetValue(root)!;
        var scale = tree.GetType().GetProperty("RootScale", Flags)!.GetValue(tree)!;
        scale.GetType().GetMethod("SetTestOverride", Flags)!.Invoke(scale, [value]);
        // Headless 没有原生 DPI 消息循环，按宿主路径发出已改变缩放的根通知。
        typeof(XamlRoot).GetMethod("RaiseChangedEvent", Flags)!.Invoke(root, null);
    }
    private static int RootSubscriptions(XamlRoot root, Image image)
        => (typeof(XamlRoot).GetField("Changed", Flags)!.GetValue(root) as Delegate)?.GetInvocationList().Count(handler =>
            handler.Target is { } target && target.GetType().DeclaringType == typeof(ManagedImageLoader)
                && ReferenceEquals(target.GetType().GetField("_image", Flags)!.GetValue(target), image)) ?? 0;
    private static object? Surface(Image image)
    {
        if (image.Source is not BitmapSource source) return null;
        var data = typeof(Microsoft.UI.Xaml.Media.ImageSource).GetField("_imageData", Flags)!.GetValue(source)!;
        return data.GetType().GetProperty("CompositionSurface", Flags)!.GetValue(data);
    }
    private static long Bytes(object? surface) => surface is null ? 0 : (long)surface.GetType().GetProperty("DecodedByteCount", Flags)!.GetValue(surface)!;
    private static int Subscriptions(Image image)
    {
        if (image.Source is not BitmapSource source) return 0;
        var subscriptions = typeof(Microsoft.UI.Xaml.Media.ImageSource).GetField("_subscriptions", Flags)!.GetValue(source)!;
        return (int)subscriptions.GetType().GetProperty("Count", Flags)!.GetValue(subscriptions)!;
    }

    internal sealed class CachedImage : IDisposable
    {
        private readonly bool _avatar;
        private readonly int _id = Interlocked.Increment(ref _nextId);
        private readonly string _url = $"https://example.invalid/{Guid.NewGuid():N}.jpg";
        private readonly int _pixelWidth;
        private readonly int _pixelHeight;
        internal int Id => _id;
        internal string Url => _url;
        internal string Path { get; }
        internal CachedImage(bool avatar, int pixelWidth = 1200, int pixelHeight = 1800)
        {
            _avatar = avatar;
            _pixelWidth = pixelWidth;
            _pixelHeight = pixelHeight;
            Path = avatar ? ImageCacheHelper.GetAvatarLocalPath(_url) : ImageCacheHelper.GetLocalPath(_id);
            WriteImage();
        }
        internal void WriteImage()
        {
            using var pixels = new SKBitmap(_pixelWidth, _pixelHeight);
            pixels.Erase(SKColors.DarkBlue);
            using var encoded = pixels.Encode(SKEncodedImageFormat.Jpeg, 90);
            using var file = File.Create(Path);
            encoded.SaveTo(file);
        }
        internal void Configure(Image image)
        {
            if (_avatar) ManagedImageLoader.ConfigureAvatar(image, _url, image.Width);
            else ManagedImageLoader.ConfigureCover(image, _id, _url, image.Width);
        }
        public void Dispose() => File.Delete(Path);
    }

    private sealed class MemorySample : IDisposable
    {
        private readonly object _gate = new();
        private readonly Timer _timer;
        internal double StartPrivate { get; }
        internal double PeakPrivate { get; private set; }
        internal double PeakWorking { get; private set; }
        internal MemorySample()
        {
            StartPrivate = Current().Private;
            _timer = new(_ => Current(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(50));
        }
        internal (double Private, double Working) Current()
        {
            lock (_gate)
            {
                using var process = Process.GetCurrentProcess();
                var memory = (Private: process.PrivateMemorySize64 / 1048576d, Working: process.WorkingSet64 / 1048576d);
                PeakPrivate = Math.Max(PeakPrivate, memory.Private);
                PeakWorking = Math.Max(PeakWorking, memory.Working);
                return memory;
            }
        }
        public void Dispose() => _timer.Dispose();
    }

    internal sealed class ImageWorkSample : IDisposable
    {
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private readonly long _allocated = GC.GetTotalAllocatedBytes(precise: false);
        private readonly int _gen2 = GC.CollectionCount(2);
        private readonly TimeSpan _cpu;
        internal ImageWorkSample() => _cpu = _process.TotalProcessorTime;
        public override string ToString()
        {
            _process.Refresh();
            return $"observeMs={_elapsed.Elapsed.TotalMilliseconds:F1}; cpuMs={(_process.TotalProcessorTime - _cpu).TotalMilliseconds:F1}; allocatedBytes={GC.GetTotalAllocatedBytes(precise: false) - _allocated}; gen2={GC.CollectionCount(2) - _gen2}";
        }
        public void Dispose() => _process.Dispose();
    }

    internal sealed class ImageReloadTrace : IDisposable
    {
        private readonly Image _image;
        private readonly long _callback;
        private readonly List<double> _actualWidths = [];
        internal List<int> Widths { get; } = [];
        internal int Opened { get; private set; }
        internal int Loaded { get; private set; }
        internal int Unloaded { get; private set; }
        internal ImageReloadTrace(Image image)
        {
            _image = image;
            _callback = image.RegisterPropertyChangedCallback(Image.SourceProperty, (_, _) =>
            {
                // 占位图也按尺寸解码；这里只统计实际内容的重载次数。
                if (image.Source is BitmapImage { DecodePixelWidth: > 0 } bitmap &&
                    bitmap.UriSource != ImageCacheHelper.PlaceholderUri)
                {
                    Widths.Add(bitmap.DecodePixelWidth);
                    bitmap.ImageOpened += (_, _) => Opened++;
                }
            });
            image.Loaded += OnLoaded;
            image.Unloaded += OnUnloaded;
            image.SizeChanged += OnSizeChanged;
        }
        private void OnLoaded(object sender, RoutedEventArgs args) => Loaded++;
        private void OnUnloaded(object sender, RoutedEventArgs args) => Unloaded++;
        private void OnSizeChanged(object sender, SizeChangedEventArgs args)
        {
            if (_actualWidths.Count < 30) _actualWidths.Add(_image.ActualWidth);
        }
        public override string ToString() => $"sources={Widths.Count}; opened={Opened}; loaded={Loaded}; unloaded={Unloaded}; decodeWidths=[{string.Join(",", Widths.Take(30))}]; actualWidths=[{string.Join(",", _actualWidths)}]";
        public void Dispose()
        {
            _image.UnregisterPropertyChangedCallback(Image.SourceProperty, _callback);
            _image.Loaded -= OnLoaded;
            _image.Unloaded -= OnUnloaded;
            _image.SizeChanged -= OnSizeChanged;
        }
    }
}
