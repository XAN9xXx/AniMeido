using AniMeido.App.Services;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.Views;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Xunit;
using static AniMeido.Desktop.Tests.DesktopTodayRecommendationPageTests;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopPaperCardNavigationTests
{
    [Fact]
    public Task MatchingPixelsDoNotLightenDuringPaperHandoff()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var black = new SolidColorBrush(Microsoft.UI.Colors.Black);
            var body = new Border { Background = black };
            var layer = new Canvas();
            var panel = new Grid { Width = 96, Height = 96, Background = new SolidColorBrush(Microsoft.UI.Colors.White),
                Children = { body, layer } };
            root.Children.Add(panel);
            var motion = new PaperCardMotion(layer, body);
            var pose = new PaperCardPose(new Rect(0, 0, 96, 96), new Rect(8, 8, 16, 20), new Rect(32, 8, 16, 20), 14);
            try
            {
                motion.Hold(new PaperCardTrip(1, "番", null, pose), pose, suspendInput: true);
                ((Border)layer.Children[0]).Background = black;
                panel.UpdateLayout();
                await CheckPixel();
                motion.RevealBody();
                await UnoVisualEvidence.WaitForAsync(() => layer.Opacity is > .25 and < .75);
                await CheckPixel();
                await UnoVisualEvidence.WaitForAsync(() => !motion.Active);
                await CheckPixel();
            }
            finally { motion.Stop(); root.Children.Remove(panel); }

            async Task CheckPixel()
            {
                var bitmap = new RenderTargetBitmap();
                await bitmap.RenderAsync(panel);
                var pixels = (await bitmap.GetPixelsAsync()).ToArray();
                int index = (bitmap.PixelWidth * (bitmap.PixelHeight / 2) + bitmap.PixelWidth / 2) * 4;
                Assert.InRange((int)pixels[index], 0, 2);
                Assert.Equal(255, pixels[index + 3]);
            }
        });

    [Fact]
    public Task OpeningTitleFollowsTheDetailHeadingWhenTheCoverFinishesLayout()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var store = new DesktopSettingsStore(fixture);
            await store.SaveAsync(new DesktopSettings { VisualStyle = "Paper" });
            await using var theme = new ThemeService(store, null, () => true);
            await theme.InitializeThemeAsync(root);
            var main = new TaskCompletionSource<Anime?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var page = fixture.Details(new Source { Detail = (_, _) => main.Task });
            page.Width = 620;
            var cover = (Border)page.FindName("CoverSection");
            cover.Height = 52;
            var trip = new PaperCardTrip(1, "作品1", null,
                new(new Rect(360, 170, 150, 230), new Rect(360, 170, 150, 195), new Rect(362, 373, 146, 22), 14));
            try
            {
                await AttachAsync(root, page);
                var load = page.OnNavigatedToAsync(trip);
                main.SetResult(Item(1));
                var layer = (Canvas)page.FindName("PaperMotionLayer");
                await UnoVisualEvidence.WaitForAsync(() => layer.Children.LastOrDefault()?.RenderTransform is CompositeTransform);
                var moving = (TextBlock)layer.Children.Last();
                var heading = (TextBlock)page.FindName("DetailTitle");
                var first = PaperCardPose.Bounds(heading, layer);
                cover.Height = 192;
                root.UpdateLayout();
                var final = PaperCardPose.Bounds(heading, layer);
                Assert.True(final.Y > first.Y + 100);
                Assert.Equal(final.Y, Canvas.GetTop(moving), 1);
                Assert.Equal(final.X, Canvas.GetLeft(moving), 1);
                var body = (ScrollViewer)page.FindName("ContentScrollViewer");
                await UnoVisualEvidence.WaitForAsync(() => layer.Opacity is > 0 and < 1);
                Assert.Equal(1, body.Opacity);
                var atHandoff = PaperCardPose.Bounds(moving, layer);
                Assert.Equal(final.X, atHandoff.X, 1);
                Assert.Equal(final.Y, atHandoff.Y, 1);
                await load;
            }
            finally { main.TrySetResult(null); await DetachAsync(root, page); }
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task ErrorOrLeavingDuringLoadRemovesTheHeldCard(bool leaveEarly)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var store = new DesktopSettingsStore(fixture);
            await store.SaveAsync(new DesktopSettings { VisualStyle = "Paper" });
            await using var theme = new ThemeService(store, null, () => true);
            await theme.InitializeThemeAsync(root);
            var pending = new TaskCompletionSource<Anime?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var page = fixture.Details(new Source { Detail = (_, _) => pending.Task });
            var trip = new PaperCardTrip(1, "作品1", null,
                new(new Rect(50, 50, 160, 250), new Rect(50, 50, 160, 220), new Rect(50, 275, 160, 25), 14));
            try
            {
                await AttachAsync(root, page);
                var load = page.OnNavigatedToAsync(trip);
                var layer = (Canvas)page.FindName("PaperMotionLayer");
                Assert.Equal(3, layer.Children.Count);
                if (leaveEarly) await DetachAsync(root, page);
                pending.SetResult(null);
                await load;
                await UnoVisualEvidence.WaitForAsync(() => layer.Children.Count == 0);
                Assert.Empty(layer.Children);
                Assert.Null(trip.Detail);
                if (!leaveEarly) Assert.Equal(Visibility.Visible, ((Grid)page.FindName("LoadingOverlay")).Visibility);
            }
            finally
            {
                pending.TrySetResult(null);
                await DetachAsync(root, page);
            }
        });

    [Theory]
    [InlineData(DesktopVisualStyle.Paper, true, false)]
    [InlineData(DesktopVisualStyle.Paper, true, true)]
    [InlineData(DesktopVisualStyle.Paper, false, false)]
    [InlineData(DesktopVisualStyle.Fluent11, true, false)]
    [InlineData(DesktopVisualStyle.Fluent10, true, false)]
    public Task SelectedCardCrossesLoadingAndReturnsOnlyForPaperWithMotion(DesktopVisualStyle style, bool motion, bool deep)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var store = new DesktopSettingsStore(fixture);
            await store.SaveAsync(new DesktopSettings { VisualStyle = style.ToString(), CatalogCardStyle = deep ? "Detailed" : "Cover" });
            await using var theme = new ThemeService(store, null, () => motion);
            await theme.InitializeThemeAsync(root);
            var main = new TaskCompletionSource<Anime?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var source = new Source { Seasonal = _ => Task.FromResult(Enumerable.Range(1, deep ? 60 : 2).Select(Item).ToList()), Detail = (_, _) => main.Task };
            var tracking = new TrackingService(fixture.Factory);
            var catalog = new PastSeasonPage(source, new CacheService(fixture.Factory),
                new DragDropService(tracking, new PersonalFeatureStateStore(fixture.Factory), NullLogger<DragDropService>.Instance),
                tracking, fixture.Navigation, NullLogger<PastSeasonPage>.Instance);
            var detail = fixture.Details(source);
            try
            {
                await AttachAsync(root, catalog);
                var id = 2;
                var scroll = (ScrollViewer)catalog.FindName("CatalogScroller");
                if (deep)
                {
                    await UnoVisualEvidence.WaitForAsync(() => scroll.ScrollableHeight > 1500);
                    scroll.ChangeView(null, 1200, null, true);
                    await UnoVisualEvidence.WaitForAsync(() => scroll.VerticalOffset > 1000);
                    root.UpdateLayout();
                    id = UnoVisualEvidence.Descendants(scroll).OfType<AnimeCard>().First(c =>
                        c.Anime is not null && c.ActualHeight > 0 && c.TransformToVisual(scroll).TransformPoint(default).Y is > 5 and < 240).Anime!.ID;
                }
                AnimeCard? Card() => UnoVisualEvidence.Descendants(catalog).OfType<AnimeCard>()
                    .FirstOrDefault(c => c.Anime?.ID == id && c.ActualWidth > 0);
                await UnoVisualEvidence.WaitForAsync(() => Card() is not null);
                new DesktopTestMouse(root).Click(Card()!);
                Assert.Equal(typeof(AnimeDetailPage), fixture.Navigation.Page);
                bool enabled = motion && style == DesktopVisualStyle.Paper;
                if (!enabled) Assert.Equal(id, fixture.Navigation.Parameter);
                await DetachAsync(root, catalog);
                await detail.OnNavigatedToAsync(fixture.Navigation.Parameter);
                await AttachAsync(root, detail);
                var layer = Assert.IsType<Canvas>(detail.FindName("PaperMotionLayer"));
                Assert.Equal(enabled, layer.Children.Count > 0);
                var loader = (Grid)detail.FindName("LoadingOverlay");
                var body = (ScrollViewer)detail.FindName("ContentScrollViewer");
                if (enabled)
                {
                    await UnoVisualEvidence.WaitForAsync(() => layer.Children.FirstOrDefault()?.RenderTransform is CompositeTransform { ScaleX: > 0 and < 1 });
                    Assert.Equal(Visibility.Collapsed, loader.Visibility);
                    Assert.Equal(0, body.Opacity);
                }
                // 深处这次模拟快命中；另一条路线等出现加载环后再给正文。
                if (!deep) await UnoVisualEvidence.WaitForAsync(() => loader.Visibility == Visibility.Visible);
                var loaderShown = false;
                var observation = loader.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => loaderShown |= loader.Visibility == Visibility.Visible);
                main.SetResult(Item(id));
                if (enabled)
                {
                    await UnoVisualEvidence.WaitForAsync(() => layer.Opacity is > 0 and < 1);
                    Assert.Equal(1, body.Opacity);
                }
                await detail.PendingActivation;
                await UnoVisualEvidence.WaitForAsync(() => layer.Children.Count == 0);
                loader.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, observation);
                if (deep) Assert.False(loaderShown);
                Assert.Equal(id, detail.ViewModel.AnimeDetail!.ID);
                Assert.Equal(1, body.Opacity);
                if (deep)
                {
                    body.ChangeView(null, 300, null, true);
                    await UnoVisualEvidence.WaitForAsync(() => body.VerticalOffset > 0);
                }
                await DetachAsync(root, detail);
                await AttachAsync(root, catalog);
                var returning = Assert.IsType<Canvas>(catalog.FindName("PaperMotionLayer"));
                Assert.Equal(enabled, returning.Children.Count > 0);
                if (enabled)
                {
                    await UnoVisualEvidence.WaitForAsync(() => returning.Children.FirstOrDefault()?.RenderTransform is CompositeTransform { ScaleX: > 1 });
                    Assert.Equal(1, ((Grid)catalog.FindName("RootGrid")).Opacity);
                    var sheet = (FrameworkElement)returning.Children[0];
                    var target = Card()!.MeasurePaperCard(returning)!;
                    Assert.Equal(target.Sheet.X, Canvas.GetLeft(sheet), 1);
                    Assert.Equal(target.Sheet.Y, Canvas.GetTop(sheet), 1);
                }
                await UnoVisualEvidence.WaitForAsync(() => returning.Children.Count == 0);
                Assert.NotNull(Card());
                if (deep) Assert.True(scroll.VerticalOffset > 1000);
            }
            finally
            {
                main.TrySetResult(Item(2));
                await DetachAsync(root, detail);
                await DetachAsync(root, catalog);
            }
        });
}
