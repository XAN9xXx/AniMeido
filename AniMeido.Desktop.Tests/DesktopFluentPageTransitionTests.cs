using AniMeido.App;
using AniMeido.App.Controls;
using AniMeido.App.Services;
using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System.Reflection;
using Windows.Foundation;
using Xunit;
using Xunit.Abstractions;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using AniMeido.Plugin.Base.Views.Controls;
using static AniMeido.Desktop.Tests.DesktopBackNavigationTests;
using static AniMeido.Desktop.Tests.DesktopTodayRecommendationPageTests;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopFluentPageTransitionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("Light", false)]
    [InlineData("Dark", false)]
    [InlineData("Light", true)]
    [InlineData("Dark", true)]
    public Task ExpandedPaperKeepsTheCoverAndBackgroundAcrossOverlayRemoval(string appearance, bool slow)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            using var cache = new DesktopManagedImageLifetimeTests.CachedImage(false, 240, 360);
            var item = Item(cache.Id) with { CoverURL = cache.Url };
            var reply = new TaskCompletionSource<Anime?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!slow) reply.SetResult(item);
            var source = new Source { Seasonal = _ => Task.FromResult(new List<Anime> { item }), Detail = (_, _) => reply.Task };
            await RunShellAsync(root, DesktopVisualStyle.Paper, true, async scene =>
            {
                var host = (Grid)scene.Shell.FindName("PageHost");
                scene.Navigation.NavigateTopLevel(typeof(PastSeasonPage));
                var catalog = (PastSeasonPage)scene.Frame.Content;
                AnimeCard? Card() => UnoVisualEvidence.Descendants(catalog).OfType<AnimeCard>().FirstOrDefault(c => c.IsLoaded && c.Anime?.ID == cache.Id
                    && c.DisplayedCover is BitmapSource { PixelWidth: > 0 });
                await UnoVisualEvidence.WaitForAsync(() => Card() is not null && scene.AtRest);
                new DesktopTestMouse(scene.Shell).Click(Card()!);
                var detail = Assert.IsType<AnimeDetailPage>(scene.Frame.Content);
                var layer = (Canvas)detail.FindName("PaperMotionLayer");
                var cover = (Border)detail.FindName("CoverSection");
                var loader = (Grid)detail.FindName("LoadingOverlay");
                var synopsis = (StackPanel)detail.FindName("SynopsisSection");
                await UnoVisualEvidence.WaitForAsync(() => layer.Children.FirstOrDefault()?.RenderTransform is CompositeTransform { ScaleX: > .98 });
                var backdrop = ((SolidColorBrush)((Border)detail.FindName("DetailBackdrop")).Background).Color;
                var samples = new List<(int B, int G, int R)>();
                var elapsed = Stopwatch.StartNew();
                bool sawBodyFade = false;
                bool removed;
                do
                {
                    if (slow && loader.Visibility == Visibility.Visible) reply.TrySetResult(item);
                    sawBodyFade |= synopsis.Opacity is > 0 and < 1;
                    removed = layer.Children.Count == 0;
                    var bitmap = new RenderTargetBitmap();
                    await bitmap.RenderAsync(host);
                    var pixels = (await bitmap.GetPixelsAsync()).ToArray();
                    int i = (bitmap.PixelWidth * 16 + bitmap.PixelWidth / 2) * 4;
                    Assert.InRange(Math.Abs(pixels[i] - backdrop.B) + Math.Abs(pixels[i + 1] - backdrop.G) + Math.Abs(pixels[i + 2] - backdrop.R), 0, 6);
                    var p = cover.TransformToVisual(host).TransformPoint(new(cover.ActualWidth / 2, cover.ActualHeight / 2));
                    i = (bitmap.PixelWidth * (int)p.Y + (int)p.X) * 4;
                    samples.Add((pixels[i], pixels[i + 1], pixels[i + 2]));
                    await Task.Delay(12);
                } while (!removed && elapsed.Elapsed < TimeSpan.FromSeconds(3));
                Assert.True(removed && sawBodyFade);
                Assert.Equal(1, synopsis.Opacity);
                // 测试封面是纯深蓝；同一张图在两层交接时也应保持深蓝。
                Assert.True(samples.All(s => s.R <= 5 && s.G <= 5 && Math.Abs(s.B - 139) <= 5),
                    $"封面中心逐帧 BGR：{string.Join(", ", samples)}");
            }, source, appearance);
        });

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public Task NewPageCompositesAboveTheOldInBothFrameOrders(string appearance)
        => InShellAsync(DesktopVisualStyle.Fluent11, true, async scene =>
        {
            var host = (Grid)scene.Shell.FindName("PageHost");
            Paint(Microsoft.UI.Colors.Red);
            foreach (int index in new[] { 1, 0 })
            {
                scene.ClickSidebar(index);
                Paint(index == 1 ? Microsoft.UI.Colors.Blue : Microsoft.UI.Colors.Red);
                await UnoVisualEvidence.WaitForAsync(() => scene.Frame.Opacity is > .2 and < .7);
                var image = new RenderTargetBitmap();
                await image.RenderAsync(host);
                var pixels = (await image.GetPixelsAsync()).ToArray();
                var i = (image.PixelWidth * (image.PixelHeight / 2) + image.PixelWidth / 2) * 4;
                Assert.InRange((int)pixels[i], 8, 247);
                Assert.InRange((int)pixels[i+2], 8, 247);
                Assert.InRange((int)pixels[i+1], 0, 2);
                await scene.UntilRestAsync();
            }
            void Paint(Windows.UI.Color color) => ((Page)scene.Frame.Content).Content = new Border { Background = new SolidColorBrush(color) };
        }, appearance: appearance);

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public Task PaperSidebarRevealsSolidNewPageFromTheSidebarWithoutMovingItsContents(string appearance)
        => InShellAsync(DesktopVisualStyle.Paper, true, async scene =>
        {
            var host = (Grid)scene.Shell.FindName("PageHost");
            Paint(Microsoft.UI.Colors.Red);
            foreach (int index in new[] { 1, 0 })
            {
                scene.ClickSidebar(index);
                Paint(index == 1 ? Microsoft.UI.Colors.Blue : Microsoft.UI.Colors.Red);
                await scene.UntilPaperRevealAsync();
                var bitmap = new RenderTargetBitmap();
                await bitmap.RenderAsync(host);
                var pixels = (await bitmap.GetPixelsAsync()).ToArray();
                var row = bitmap.PixelWidth * (bitmap.PixelHeight / 2);
                AssertPixel(row + bitmap.PixelWidth / 12, index == 1);
                AssertPixel(row + bitmap.PixelWidth * 11 / 12, index == 0);
                await scene.UntilRestAsync();

                void AssertPixel(int pixel, bool blue)
                {
                    Assert.Equal(blue ? 255 : 0, pixels[pixel * 4]);
                    Assert.Equal(blue ? 0 : 255, pixels[pixel * 4 + 2]);
                }
            }
            void Paint(Windows.UI.Color color) => ((Page)scene.Frame.Content).Content = new Border { Background = new SolidColorBrush(color) };
        }, appearance: appearance);

    [Theory]
    [InlineData(DesktopVisualStyle.Paper, false)]
    [InlineData(DesktopVisualStyle.Fluent10, false)]
    [InlineData(DesktopVisualStyle.Fluent11, true)]
    public async Task LibraryDetailHandoffReleasesTheListEvenBeforeDataAndBackRestoresTheCard(DesktopVisualStyle style, bool earlyBack)
    {
        var reply = new TaskCompletionSource<Anime?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source { Seasonal = _ => Task.FromResult(new List<Anime> { Item(42) }), Detail = (_, _) => reply.Task };
        await InShellAsync(style, true, async scene =>
        {
            scene.Navigation.NavigateTopLevel(typeof(PastSeasonPage));
            var catalog = Assert.IsType<PastSeasonPage>(scene.Frame.Content);
            AnimeCard? Card() => UnoVisualEvidence.Descendants(catalog).OfType<AnimeCard>().FirstOrDefault(c => c.IsLoaded && c.ActualWidth > 0 && c.Anime?.ID == 42);
            await UnoVisualEvidence.WaitForAsync(() => Card() is not null && scene.AtRest);
            var input = new DesktopTestMouse(scene.Shell);
            input.Click(Card()!);
            var detail = Assert.IsType<AnimeDetailPage>(scene.Frame.Content);
            try
            {
                Assert.True(catalog.IsLoaded);
                if (!earlyBack)
                {
                    if (style == DesktopVisualStyle.Paper)
                        await UnoVisualEvidence.WaitForAsync(() => catalog.IsLoaded && detail.IsPaperCardOpening && scene.Frame.Opacity == 1);
                    await UnoVisualEvidence.WaitForAsync(() => !catalog.IsLoaded);
                    Assert.False(reply.Task.IsCompleted);
                    await UnoVisualEvidence.WaitForAsync(() => ((Grid)detail.FindName("LoadingOverlay")).Visibility == Visibility.Visible);
                    reply.SetResult(Item(42));
                    await detail.PendingActivation;
                }
                if (style == DesktopVisualStyle.Paper)
                    ((Border)detail.FindName("DetailBackdrop")).Background = new SolidColorBrush(Microsoft.UI.Colors.Magenta);
                await scene.BackToAsync(catalog);
                if (style == DesktopVisualStyle.Paper)
                {
                    var layer = (Canvas)catalog.FindName("PaperMotionLayer");
                    await UnoVisualEvidence.WaitForAsync(() => layer.Children.FirstOrDefault()?.RenderTransform is CompositeTransform { ScaleX: > 1 });
                    var host = (Grid)scene.Shell.FindName("PageHost");
                    var background = ((SolidColorBrush)((Grid)catalog.FindName("RootGrid")).Background).Color;
                    var clock = Stopwatch.StartNew();
                    bool sampledMargin = false;
                    do
                    {
                        Assert.Equal(1, scene.Frame.Opacity);
                        Assert.Equal(1, layer.Opacity);
                        Assert.False(detail.IsLoaded);
                        // 列表边缘的留白应该一直是纸色，即使旧详情铺着不同的底色。
                        if (layer.Children.FirstOrDefault() is Border sheet && PaperCardPose.Bounds(sheet, host).Right < host.ActualWidth - 12)
                        {
                            var bitmap = new RenderTargetBitmap();
                            await bitmap.RenderAsync(host);
                            var pixels = (await bitmap.GetPixelsAsync()).ToArray();
                            var i = (bitmap.PixelWidth * (bitmap.PixelHeight / 2) + bitmap.PixelWidth - 8) * 4;
                            Assert.InRange(Math.Abs(pixels[i] - background.B) + Math.Abs(pixels[i+1] - background.G)
                                + Math.Abs(pixels[i+2] - background.R), 0, 6);
                            sampledMargin = true;
                        }
                        await Task.Delay(12);
                    } while (layer.Children.Count > 0 && clock.Elapsed < TimeSpan.FromSeconds(3));
                    Assert.Empty(layer.Children);
                    Assert.True(sampledMargin);
                }
                if (earlyBack) Assert.Equal(1, scene.Frame.Opacity);
                await UnoVisualEvidence.WaitForAsync(() => Card() is not null && !detail.IsLoaded && scene.AtRest);
                Assert.False(scene.Navigation.CanGoBack);
            }
            finally { reply.TrySetResult(Item(42)); await detail.PendingActivation; }
        }, source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PaperMovesBetweenCalendarCatalogAndOrdinaryDetail(bool slowCalendar)
    {
        var schedule = new TaskCompletionSource<List<Anime>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source { Seasonal = _ => Task.FromResult(new List<Anime> { Item(42) }),
            Schedule = (_, _) => slowCalendar ? schedule.Task : Task.FromResult(new List<Anime> { Item(42) }) };
        return InShellAsync(DesktopVisualStyle.Paper, true, async scene =>
        {
            try
            {
                scene.ClickSidebar(2);
                await scene.UntilPaperRevealAsync();
                await scene.UntilRestAsync();
                scene.ClickSidebar(3);
                var calendar = Assert.IsType<CurrentSeasonPage>(scene.Frame.Content);
                await scene.UntilPaperRevealAsync();
                await scene.UntilRestAsync();
                schedule.TrySetResult([Item(42)]);
                await calendar.PendingActivation;
                scene.Navigation.Navigate(typeof(AnimeDetailPage), 42);
                await scene.UntilMovingAsync(t => t.TranslateX > 0);
                await scene.UntilRestAsync();
                await scene.BackToAsync(calendar);
                await scene.UntilMovingAsync(t => t.TranslateX < 0);
                await scene.UntilRestAsync();
            }
            finally { schedule.TrySetResult([]); }
        }, source);
    }

    [Theory]
    [InlineData(DesktopVisualStyle.Fluent10, "Light")]
    [InlineData(DesktopVisualStyle.Fluent10, "Dark")]
    [InlineData(DesktopVisualStyle.Fluent11, "Dark")]
    [InlineData(DesktopVisualStyle.Fluent11, "Light")]
    [InlineData(DesktopVisualStyle.Paper, "Light")]
    [InlineData(DesktopVisualStyle.Paper, "Dark")]
    public Task SidebarKeepsOnlyOneOutgoingPageUntilTheIncomingSurfaceIsVisible(DesktopVisualStyle style, string appearance)
        => InShellAsync(style, true, async scene =>
        {
            var first = (Page)scene.Frame.Content;
            scene.ClickSidebar(1);
            var frames = UnoVisualEvidence.Descendants((Border)scene.Shell.FindName("Workspace")).OfType<Frame>().ToArray();
            Assert.Equal(2, frames.Count(f => f.Content is Page));
            var old = Assert.Single(frames, f => ReferenceEquals(f.Content, first));
            Assert.True(first.IsLoaded);
            Assert.False(old.IsHitTestVisible);
            Assert.Equal(1, old.Opacity);
            var incoming = Assert.Single(frames, f => f.Content is SearchStatePage);
            Assert.Equal(0, incoming.Opacity);
            Assert.False(incoming.IsHitTestVisible);
            if (style == DesktopVisualStyle.Paper) await scene.UntilPaperRevealAsync();
            else await UnoVisualEvidence.WaitForAsync(() => incoming.Opacity is > 0 and < 1);
            Assert.Equal(1, old.Opacity);
            scene.ClickSidebar(0);
            scene.ClickSidebar(1);
            await UnoVisualEvidence.WaitForAsync(() => frames.Count(f => f.Content is Page) == 1);
            Assert.IsType<SearchStatePage>(Assert.Single(frames, f => f.Content is Page).Content);
            Assert.False(first.IsLoaded);
        }, appearance: appearance);

    [Fact]
    public Task DisablingMotionDuringAnUnarrangedSwitchImmediatelyReleasesTheOldPage()
        => InShellAsync(DesktopVisualStyle.Paper, true, async scene =>
        {
            var previous = (Page)scene.Frame.Content;
            scene.ClickSidebar(1);
            Assert.Equal(0, scene.Frame.Opacity);
            scene.SystemMotion = false;
            scene.Theme.RefreshSystemMotionPreference();
            Assert.True(scene.AtRest && scene.Frame.IsHitTestVisible);
            await UnoVisualEvidence.WaitForAsync(() => !previous.IsLoaded);
            await Task.Delay(220);
            Assert.True(scene.AtRest && scene.Frame.IsHitTestVisible);
        });

    [Fact]
    [Trait("Category", "PerformanceDiagnostic")]
    public Task CatalogSwitchesMeasurePeakMemoryAndUiThreadGaps()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            // 同一套封面、固定视口，同一进程内预热后再采样。每次比较使用新的测试宿主。
            var covers = Enumerable.Range(0, 48).Select(_ => new DesktopManagedImageLifetimeTests.CachedImage(false, 360, 540)).ToArray();
            try
            {
                var entries = covers.Select(c => Item(c.Id) with { CoverURL = c.Url }).ToList();
                await RunShellAsync(root, DesktopVisualStyle.Paper, true, async scene =>
                {
                    await SwitchAsync(null);
                    using var process = Process.GetCurrentProcess();
                    long last = 0;
                    var gaps = new List<double>();
                    process.Refresh();
                    long baseline = process.PrivateMemorySize64, peak = baseline;
                    long navigationStart = 0;
                    string direction = "";
                    var slow = new List<(string Direction, double SinceClick, double Gap)>();
                    void Sample()
                    {
                        long now = Stopwatch.GetTimestamp();
                        double gap = Stopwatch.GetElapsedTime(last, now).TotalMilliseconds;
                        gaps.Add(gap);
                        if (gap >= 50)
                            slow.Add((direction, Stopwatch.GetElapsedTime(navigationStart, now).TotalMilliseconds, gap));
                        last = now;
                        process.Refresh();
                        peak = Math.Max(peak, process.PrivateMemorySize64);
                    }
                    for (int i = 0; i < 4; i++) await SwitchAsync(Sample);
                    gaps.Sort();
                    output.WriteLine($"catalog: peak={peak / 1048576d:F1} MiB; aboveWarm={(peak-baseline)/1048576d:F1} MiB; UI p95={gaps[(int)(gaps.Count*.95)]:F1} ms; max={gaps[^1]:F1} ms; samples={gaps.Count}");
                    output.WriteLine($"slow intervals >=50ms: {slow.Count}; " + string.Join("; ", slow.Select(s =>
                        $"{s.Direction} +{s.SinceClick:F0}ms: {s.Gap:F1}ms")));
                    async Task SwitchAsync(Action? sample)
                    {
                        foreach (var type in new[] { typeof(PastSeasonPage), typeof(SearchStatePage) })
                        {
                            direction = type == typeof(PastSeasonPage) ? "enter" : "leave";
                            last = navigationStart = Stopwatch.GetTimestamp();
                            scene.Navigation.NavigateTopLevel(type);
                            var mount = Stopwatch.GetElapsedTime(navigationStart).TotalMilliseconds;
                            for (int j = 0; j < 32; j++) { await Task.Delay(16); sample?.Invoke(); }
                            if (type == typeof(PastSeasonPage))
                            {
                                var cards = UnoVisualEvidence.Descendants(scene.Frame).OfType<AnimeCard>().Count(c => c.IsLoaded && c.Anime is not null);
                                Assert.True(cards > 0);
                                if (sample is not null) output.WriteLine($"catalog entry: mount={mount:F1}ms, realized={cards}");
                            }
                        }
                    }
                }, new Source { Seasonal = _ => Task.FromResult(entries) }, "Light");
            }
            finally { foreach (var cover in covers) cover.Dispose(); }
        });
    [Theory]
    [InlineData(DesktopVisualStyle.Fluent10)]
    [InlineData(DesktopVisualStyle.Fluent11)]
    public Task SidebarDrillAndBackAnimateOnlyTheCurrentContent(DesktopVisualStyle style)
        => InShellAsync(style, true, async scene =>
        {
            Assert.IsType<DetailStatePage>(scene.Frame.Content);
            Assert.True(scene.AtRest);

            scene.ClickSidebar(1);
            Assert.InRange(scene.Frame.Opacity, 0, .15);
            var list = Assert.IsType<SearchStatePage>(scene.Frame.Content);
            list.Query.Text = "保留筛选词";
            await scene.UntilMovingAsync(t => style == DesktopVisualStyle.Fluent10
                ? t.ScaleX is > .94 and < 1 && t.ScaleX == t.ScaleY && t.TranslateX == 0 && t.TranslateY == 0
                : t.TranslateY is > 0 and < 38 && t.TranslateX == 0 && t.ScaleX == 1);
            Assert.Equal(new Point(.5, .5), scene.Frame.RenderTransformOrigin);
            await scene.UntilRestAsync();

            var contentCenter = scene.ContentCenter();
            scene.Navigation.Navigate(typeof(DetailStatePage), 42);
            // 在让出 UI 线程之前，新内容就应处于起点；第一帧不能先在终点显现。
            Assert.InRange(scene.Frame.Opacity, 0, .15);
            var start = Assert.IsType<CompositeTransform>(scene.Frame.RenderTransform);
            if (style == DesktopVisualStyle.Fluent10) Assert.InRange(start.ScaleX, .939, .95);
            else Assert.InRange(start.TranslateX, 48, 58);
            await CheckDepthAsync(forward: true);
            await scene.UntilRestAsync();
            await scene.BackToAsync(list);
            await CheckDepthAsync(forward: false);
            await scene.UntilRestAsync();
            Assert.Equal("保留筛选词", list.Query.Text);
            Assert.False(scene.Navigation.CanGoBack);

            async Task CheckDepthAsync(bool forward)
            {
                if (style == DesktopVisualStyle.Fluent10)
                {
                    await scene.UntilMovingAsync(t => t.ScaleX == t.ScaleY && t.TranslateX == 0 && t.TranslateY == 0
                        && (forward ? t.ScaleX is > .94 and < 1 : t.ScaleX is > 1 and < 1.06));
                    var during = scene.ContentCenter();
                    Assert.InRange(Math.Abs(contentCenter.X - during.X), 0, 1);
                    Assert.InRange(Math.Abs(contentCenter.Y - during.Y), 0, 1);
                }
                else
                {
                    await scene.UntilMovingAsync(t => t.ScaleX == 1 && t.TranslateY == 0
                        && (forward ? t.TranslateX is > 0 and < 58 : t.TranslateX is > -58 and < 0));
                }
            }
        });

    [Theory]
    [InlineData(DesktopVisualStyle.Fluent10, false)]
    [InlineData(DesktopVisualStyle.Fluent11, false)]
    [InlineData(DesktopVisualStyle.Paper, false)]
    public Task ReducedMotionShowsTheDestinationImmediately(DesktopVisualStyle style, bool motion)
        => InShellAsync(style, motion, async scene =>
        {
            scene.ClickSidebar(1);
            var list = Assert.IsType<SearchStatePage>(scene.Frame.Content);
            Assert.True(scene.AtRest);
            scene.Navigation.Navigate(typeof(DetailStatePage), 42);
            Assert.IsType<DetailStatePage>(scene.Frame.Content);
            Assert.True(scene.AtRest);
            await scene.BackToAsync(list);
            Assert.True(scene.AtRest);
            await Task.Delay(30);
            Assert.True(scene.AtRest);
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task NewNavigationReplacesAMovingPageAndChangingPreferencesSettlesIt(bool reduceMotion)
        => InShellAsync(DesktopVisualStyle.Fluent11, true, async scene =>
        {
            scene.ClickSidebar(1);
            var list = Assert.IsType<SearchStatePage>(scene.Frame.Content);
            scene.Navigation.Navigate(typeof(DetailStatePage), 42);
            await scene.UntilMovingAsync(t => t.TranslateX > 0);
            await scene.BackToAsync(list);
            await scene.UntilMovingAsync(t => t.TranslateX < 0);

            if (reduceMotion)
            {
                scene.SystemMotion = false;
                scene.Theme.RefreshSystemMotionPreference();
            }
            else
                await scene.Theme.SetVisualStyleAsync(DesktopVisualStyle.Paper);

            Assert.True(scene.AtRest);
            await Task.Delay(320);
            Assert.True(scene.AtRest);
            Assert.Same(list, scene.Frame.Content);
            Assert.False(scene.Navigation.CanGoBack);
        });

    [Theory]
    [InlineData(DesktopVisualStyle.Fluent10)]
    [InlineData(DesktopVisualStyle.Fluent11)]
    public Task SlowDetailsKeepTheFrameStillAndAnimateTheBodyWhenItsDataArrives(DesktopVisualStyle style)
        => WithSlowDetailsAsync(style, true, async (scene, page, reply) =>
        {
            var layer = (Grid)page.FindName("LoadingOverlay");
            var ring = (ProgressRing)page.FindName("LoadingRing");
            var body = (ScrollViewer)page.FindName("ContentScrollViewer");
            Assert.True(scene.AtRest);
            await UnoVisualEvidence.WaitForAsync(() => layer.Visibility == Visibility.Visible && layer.Opacity is > 0 and < 1);
            await UnoVisualEvidence.WaitForAsync(() => layer.Opacity == 1 && ring.IsActive);
            // 比导航动画留得更久，正文仍未到达。
            await Task.Delay(320);
            Assert.True(scene.AtRest);
            Assert.False(page.PendingActivation.IsCompleted);

            reply.SetResult(Item(42));
            bool sawExit = false, sawEntry = false;
            await UnoVisualEvidence.WaitForAsync(() =>
            {
                if (layer.Visibility == Visibility.Visible && layer.Opacity is > 0 and < 1)
                {
                    Assert.False(layer.IsHitTestVisible);
                    sawExit = true;
                }
                if (body.Visibility == Visibility.Visible && body.Opacity is > 0 and < 1
                    && body.RenderTransform is CompositeTransform t)
                {
                    Assert.True(scene.AtRest);
                    sawEntry |= style == DesktopVisualStyle.Fluent10
                        ? t.ScaleX is > .94 and < 1 && t.ScaleY == t.ScaleX && t.TranslateX == 0
                        : t.TranslateX is > 0 and < 58 && t.ScaleX == 1;
                }
                return sawExit && sawEntry;
            });
            await UnoVisualEvidence.WaitForAsync(() => layer.Visibility == Visibility.Collapsed && IsAtRest(body));
            Assert.False(ring.IsActive);
            Assert.Equal(42, page.ViewModel.AnimeDetail?.ID);
        });

    [Fact]
    public Task SlowDetailsWithMotionDisabledReplaceTheLoadingLayerImmediately()
        => WithSlowDetailsAsync(DesktopVisualStyle.Fluent11, false, async (scene, page, reply) =>
        {
            var layer = (Grid)page.FindName("LoadingOverlay");
            var body = (ScrollViewer)page.FindName("ContentScrollViewer");
            await UnoVisualEvidence.WaitForAsync(() => layer.Visibility == Visibility.Visible);
            Assert.Equal(1, layer.Opacity);
            reply.SetResult(Item(42));
            await UnoVisualEvidence.WaitForAsync(() => body.Visibility == Visibility.Visible);
            Assert.Equal(Visibility.Collapsed, layer.Visibility);
            Assert.True(scene.AtRest && IsAtRest(body));
        });

    [Fact]
    public Task LateDetailDataDoesNotMoveOrCoverTheNewSidebarPage()
        => WithSlowDetailsAsync(DesktopVisualStyle.Fluent11, true, async (scene, page, reply) =>
        {
            var layer = (Grid)page.FindName("LoadingOverlay");
            var previousBody = (ScrollViewer)page.FindName("ContentScrollViewer");
            await UnoVisualEvidence.WaitForAsync(() => layer.IsLoaded && layer.Visibility == Visibility.Visible);
            scene.ClickSidebar(1);
            var next = Assert.IsType<SearchStatePage>(scene.Frame.Content);
            next.Query.Text = "继续搜索";
            await scene.UntilRestAsync();
            reply.SetResult(Item(42));
            await page.PendingActivation;
            Assert.Same(next, scene.Frame.Content);
            Assert.Equal("继续搜索", next.Query.Text);
            Assert.True(scene.AtRest && IsAtRest(previousBody));
            Assert.False(layer.IsLoaded);
        });

    private static Task WithSlowDetailsAsync(DesktopVisualStyle style, bool motion,
        Func<Scene, AnimeDetailPage, TaskCompletionSource<Anime?>, Task> check)
    {
        var reply = new TaskCompletionSource<Anime?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source { Detail = (_, _) => reply.Task };
        return InShellAsync(style, motion, async scene =>
        {
            scene.Navigation.Navigate(typeof(AnimeDetailPage), 42);
            var page = Assert.IsType<AnimeDetailPage>(scene.Frame.Content);
            try { await check(scene, page, reply); }
            finally
            {
                reply.TrySetResult(Item(42));
                await page.PendingActivation;
            }
        }, source);
    }

    private static Task InShellAsync(DesktopVisualStyle style, bool motion, Func<Scene, Task> check, Source? details = null, string appearance = "System")
        => UnoHeadlessTestHost.RunAsync(root => RunShellAsync(root, style, motion, check, details, appearance));

    private static async Task RunShellAsync(Grid root, DesktopVisualStyle style, bool motion, Func<Scene, Task> check, Source? details, string appearance)
        {
            await using var fixture = await Fixture.CreateAsync();
            var settings = new DesktopSettingsStore(fixture);
            await settings.SaveAsync(new DesktopSettings { VisualStyle = style.ToString(), ThemeSetting = appearance, PrivacyAccepted = true, AnnouncementShown = true });
            var scene = new Scene { SystemMotion = motion };
            await using var theme = new ThemeService(settings, null, () => scene.SystemMotion);
            var services = new ServiceCollection();
            NavigationService? navigation = null;
            if (details is not null)
            {
                services.AddSingleton<IAnimeDataSource>(details);
                services.AddSingleton(new TrackingService(fixture.Factory));
                services.AddSingleton(new SavedTagService(fixture.Factory));
                services.AddSingleton(new BrowseHistoryService(fixture.Factory));
                services.AddSingleton(new ArchiveService(fixture.Factory));
                services.AddSingleton<IPluginNavigator>(_ => navigation!);
                services.AddSingleton(fixture.CreateRatingSource());
                services.AddSingleton<ILogger<AnimeDetailPage>>(NullLogger<AnimeDetailPage>.Instance);
                services.AddSingleton(new CacheService(fixture.Factory));
                services.AddSingleton(new DragDropService(new TrackingService(fixture.Factory), new PersonalFeatureStateStore(fixture.Factory), NullLogger<DragDropService>.Instance));
                services.AddSingleton<ILogger<PastSeasonPage>>(NullLogger<PastSeasonPage>.Instance);
                services.AddSingleton(new PersonalFeatureStateStore(fixture.Factory));
                services.AddSingleton<ILogger<CurrentSeasonPage>>(NullLogger<CurrentSeasonPage>.Instance);
            }
            using var provider = services.BuildServiceProvider();
            navigation = new NavigationService(new PageFactory(provider), NullLogger<NavigationService>.Instance);
            var items = new PluginContributionRegistry();
            items.SetBuiltInItems([
                PluginNavigationItem.CreatePage("首页", "", "Home", typeof(DetailStatePage)),
                PluginNavigationItem.CreatePage("列表", "", "List", typeof(SearchStatePage)),
                PluginNavigationItem.CreatePage("番剧库", "", "Catalog", typeof(PastSeasonPage)),
                PluginNavigationItem.CreatePage("放送日历", "", "Calendar", typeof(CurrentSeasonPage)),
            ]);
            var drag = new DragDropService(new TrackingService(fixture.Factory), new PersonalFeatureStateStore(fixture.Factory),
                NullLogger<DragDropService>.Instance);
            var window = new MainWindow(items, navigation, theme, new StartupDialogCoordinator(new PrivacyService(settings), settings),
                new MainWindowDragDropAdapter(drag));
            var shell = Assert.IsType<Grid>(window.Content);
            await theme.InitializeThemeAsync(shell, window.FeedbackSurface);
            scene.Shell = shell;
            scene.Navigation = navigation;
            scene.Theme = theme;
            root.Children.Add(shell);
            try
            {
                await UnoVisualEvidence.WaitForAsync(() => scene.Frame.IsLoaded && navigation.IsFirstNavigationCompleted);
                await window.WaitForStartupAsync();
                root.UpdateLayout();
                await check(scene);
            }
            finally { window.BeginShutdown(); root.Children.Remove(shell); window.Close(); }
        }

    private static bool IsAtRest(FrameworkElement element) => element.Opacity == 1 && (element.RenderTransform is null
        or CompositeTransform { ScaleX: 1, ScaleY: 1, TranslateX: 0, TranslateY: 0 });

    private sealed class Scene
    {
        internal bool SystemMotion { get; set; }
        internal Grid Shell { get; set; } = null!;
        internal NavigationService Navigation { get; set; } = null!;
        internal ThemeService Theme { get; set; } = null!;
        internal Frame Frame => Navigation.ActiveFrame!;
        internal bool AtRest => Frame.Clip is null && IsAtRest(Frame);

        internal void ClickSidebar(int index)
        {
            var buttons = (StackPanel)Shell.FindName("NavigationItems");
            var button = Assert.IsType<DesktopShellNavigationItem>(buttons.Children[index]);
            typeof(ButtonBase).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, null);
        }

        internal async Task BackToAsync(Page previous)
        {
            var button = (Button)Shell.FindName("BackButton");
            Assert.True(button.IsEnabled);
            Invoke(button);
            await UnoVisualEvidence.WaitForAsync(() => ReferenceEquals(previous, Frame.Content));
        }

        internal Point ContentCenter()
        {
            Assert.True(Frame.ActualWidth > 0 && Frame.ActualHeight > 0);
            return Frame.TransformToVisual((Border)Shell.FindName("Workspace"))
                .TransformPoint(new Point(Frame.ActualWidth / 2, Frame.ActualHeight / 2));
        }

        internal Task UntilRestAsync() => UnoVisualEvidence.WaitForAsync(() => AtRest);

        internal async Task UntilPaperRevealAsync()
        {
            await UnoVisualEvidence.WaitForAsync(() => Frame.Clip is { } clip && Frame.ActualWidth > 0
                && clip.Rect.Width / Frame.ActualWidth is > .2 and < .7);
            Assert.Equal(1, Frame.Opacity);
            Assert.Null(Frame.RenderTransform);
        }

        internal async Task UntilMovingAsync(Func<CompositeTransform, bool> direction)
        {
            await UnoVisualEvidence.WaitForAsync(() => Frame.Opacity is > 0 and < 1
                && Frame.RenderTransform is CompositeTransform t && direction(t));
            Assert.Equal(1, ((Grid)Shell.FindName("NavigationPane")).Opacity);
            Assert.Null(((Grid)Shell.FindName("ShellBody")).RenderTransform);
            Assert.Null(((Grid)Shell.FindName("TitleBarHost")).RenderTransform);
        }
    }
}
