using AniMeido.App.Services;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using AniMeido.Plugin.Base.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media.Imaging;
using AniMeido.Plugin.Base.Views.Controls;
using SkiaSharp;
using System.Runtime.InteropServices.WindowsRuntime;
using Xunit;
using static AniMeido.Desktop.Tests.DesktopTodayRecommendationPageTests;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopTodayRecommendationRenderTests
{
    public static TheoryData<DesktopVisualStyle, bool, int, bool> Modes => new()
    {
        { DesktopVisualStyle.Fluent10, false, 860, false }, { DesktopVisualStyle.Fluent10, true, 860, false },
        { DesktopVisualStyle.Fluent11, false, 860, false }, { DesktopVisualStyle.Fluent11, true, 860, false },
        { DesktopVisualStyle.Paper, false, 860, false }, { DesktopVisualStyle.Paper, true, 860, false },
        { DesktopVisualStyle.Fluent10, false, 1280, false }, { DesktopVisualStyle.Fluent10, true, 1280, false },
        { DesktopVisualStyle.Fluent11, false, 1280, false }, { DesktopVisualStyle.Fluent11, true, 1280, false },
        { DesktopVisualStyle.Paper, false, 1280, false }, { DesktopVisualStyle.Paper, true, 1280, false },
        { DesktopVisualStyle.Paper, false, 1280, true }, { DesktopVisualStyle.Paper, true, 860, true },
        { DesktopVisualStyle.Fluent11, false, 1280, true }, { DesktopVisualStyle.Fluent10, true, 860, true },
    };

    [Theory]
    [MemberData(nameof(Modes))]
    public Task BothPagesRenderTheirOwnContentAndDesktopLayout(DesktopVisualStyle visual, bool dark, int width, bool serif)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var settings = new DesktopSettingsStore(fixture);
            await settings.SaveAsync(new DesktopSettings { VisualStyle = visual.ToString(), ThemeSetting = dark ? "Dark" : "Light", FontStyle = serif ? "Serif" : "System" });
            await using var theme = new ThemeService(settings);
            await theme.InitializeThemeAsync(root);
            var suffix = serif ? "-serif" : "";
            SeedCoverImages();
            await new TrackingService(fixture.Factory).SetStatusAsync(1, AnimeTrackingStatus.Following);
            await new BrowseHistoryService(fixture.Factory).RecordAsync(16, "没有封面的长标题作品用来检查两行截断");
            var action = new ActionCenterService(fixture.Factory);
            foreach (var id in Enumerable.Range(2, 5)) await action.UpsertPlanAsync(id, $"计划{id}", AnimePlanPriority.Normal, null, id);
            const string browseTitle = "没有封面的长标题作品用来检查两行截断";
            var today = fixture.Today(new Source
            {
                Schedule = (_, _) => Task.FromResult(new List<Anime> { VisualItem(1) }),
                Detail = (id, _) => Task.FromResult<Anime?>(id == 16 ? Item(id) with { Title = browseTitle, CoverURL = null } : VisualItem(id)),
            });
            today.Width = width;
            today.Height = 900;
            try
            {
                await AttachAsync(root, today);
                await today.PendingActivation;
                await today.ViewModel.Theme.PendingLoad;
                today.ViewModel.Theme.Title = "在宇宙中旅行";
                today.ViewModel.Theme.Items = new(Enumerable.Range(10, 6).Select(id => new TodayThemeItem(VisualItem(id), "", "", TodayThemeRowKind.Mark)));
                root.UpdateLayout();
                await Task.Delay(20);
                Assert.Equal(width < 1088 ? 1 : 0, Grid.GetRow((Grid)today.FindName("PlanCard")));
                Assert.Equal(1, Assert.Single(today.ViewModel.PersonalBroadcasts).Anime.ID);
                var broadcastHost = (Grid)today.FindName("TodayContent");
                var broadcasts = (ItemsRepeater)today.FindName("PersonalBroadcastList");
                Assert.InRange(broadcasts.ActualWidth, broadcastHost.ActualWidth / 2 - 5, broadcastHost.ActualWidth / 2);
                Assert.NotNull(broadcasts.TryGetElement(0));
                Assert.Null(broadcasts.TryGetElement(1));
                var recent = (ScrollViewer)today.FindName("RecentBrowseScroll");
                Assert.Equal(ScrollBarVisibility.Hidden, recent.HorizontalScrollBarVisibility);
                var entry = Assert.Single(VisualChildren<Button>(recent), button => AutomationProperties.GetName(button) == browseTitle);
                Assert.Equal(196, entry.ActualWidth, 0);
                Assert.True(entry.IsTabStop);
                var initial = Assert.Single(VisualChildren<TextBlock>(entry), text => text.Name == "BrowseInitial");
                Assert.Equal("没", initial.Text);
                Assert.Equal(0, Assert.Single(VisualChildren<Image>(entry), image => image.Name == "BrowseCover").Opacity);
                Assert.Equal(2, Assert.Single(VisualChildren<TextBlock>(entry), text => text.Name == "BrowseTitle").MaxLines);
                await Task.Delay(80);
                await RenderAsync(today, $"today-{visual}-{dark}-{width}{suffix}");
                await today.OpenPlansAsync();
                await Task.Delay(20);
                await RenderAsync((Grid)today.FindName("PlanVisual"), $"plans-expanded-{visual}-{dark}-{width}{suffix}");
            }
            finally { await DetachAsync(root, today); await today.PendingActivation; await today.ViewModel.Theme.PendingLoad; }

            var state = State();
            state.Snapshot = state.Snapshot! with { Items = state.Snapshot.Items.Select(item => item with { Anime = VisualItem(item.Anime.ID) }).ToArray() };
            var feed = fixture.Recommendation(new Source { Tags = (_, _) => Task.FromResult(new List<Tag> { new("科幻"), new("冒险"), new("治愈"), new("音乐") }) }, state);
            feed.Width = width;
            feed.Height = 1100;
            try
            {
                await AttachAsync(root, feed);
                await feed.PendingActivation;
                root.UpdateLayout();
                await Task.Delay(20);
                Assert.Equal("星海中的远行", ((StackPanel)feed.FindName("SelectedPreviewContent")).Children.OfType<TextBlock>().First().Text);
                Assert.Equal("1 / 3", ((TextBlock)feed.FindName("DesktopRecommendationPageText")).Text);
                var candidates = (ItemsRepeater)feed.FindName("RecommendationCandidates");
                var candidate = Assert.IsType<AnimeCard>(candidates.TryGetElement(0));
                Assert.True(candidate.IsSelected);
                Assert.Equal(124, ((Grid)candidate.FindName("CoverHost")).ActualWidth, 0);
                Assert.Equal(186, ((Grid)candidate.FindName("CoverHost")).ActualHeight, 0);
                var selection = (Border)candidate.FindName("CoverSelection");
                var edge = (Border)candidate.FindName("CoverEdge");
                Assert.Equal(visual == DesktopVisualStyle.Paper ? Visibility.Visible : Visibility.Collapsed, selection.Visibility);
                Assert.Equal(new Thickness(visual == DesktopVisualStyle.Paper ? 1 : 2), edge.BorderThickness);
                var next = Assert.IsType<AnimeCard>(candidates.TryGetElement(1));
                Assert.Equal(136, next.TransformToVisual(candidate).TransformPoint(new Windows.Foundation.Point()).X, 0);
                if (visual == DesktopVisualStyle.Paper)
                {
                    Assert.True(selection.ActualHeight > 186);
                    var viewport = (ScrollViewer)feed.FindName("CandidateScroll");
                    var backplate = selection.TransformToVisual(viewport).TransformPoint(new Windows.Foundation.Point());
                    Assert.True(backplate.X >= 0 && backplate.Y >= 0);
                }
                Assert.Equal((CornerRadius)Application.Current.Resources["DesktopCoverRadius"], ((Border)candidate.FindName("CardSurface")).CornerRadius);
                Assert.Equal("星海中的远行", ((TextBlock)candidate.FindName("TitleText")).Text);
                Assert.True(((TextBlock)candidate.FindName("TitleText")).ActualHeight > 0);
                await Task.Delay(80);
                await RenderAsync(feed, $"recommendation-{visual}-{dark}-{width}{suffix}");
                ((Expander)feed.FindName("TagPreferencesExpander")).IsExpanded = true;
                root.UpdateLayout();
                await Task.Delay(20);
                await RenderAsync((Grid)feed.FindName("TagPackVisual"), $"tags-expanded-{visual}-{dark}-{width}{suffix}");
            }
            finally { await DetachAsync(root, feed); await feed.PendingTags; await feed.PendingPrefetch; }
        });

    [Theory]
    [InlineData(440, false)]
    [InlineData(860, true)]
    public Task TodayBroadcastStacksOnlyTheMarkedTitlesWhenSpaceIsTight(int width, bool twoColumns)
        => DesktopInteractionRenderTests.WithThemeAsync(DesktopVisualStyle.Paper, true, async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var tracking = new TrackingService(fixture.Factory);
            await tracking.SetStatusAsync(1, AnimeTrackingStatus.Watching);
            await tracking.SetStatusAsync(2, AnimeTrackingStatus.Following);
            var works = Enumerable.Range(1, 3).Select(id => Item(id) with
            {
                Title = $"作品{id}：有两行可读的放送作品标题",
                CoverURL = null,
            }).ToList();
            var today = fixture.Today(new Source { Schedule = (_, _) => Task.FromResult(works) });
            today.Width = width;
            try
            {
                await AttachAsync(root, today);
                await today.PendingActivation;
                root.UpdateLayout();
                await Task.Delay(30);
                root.UpdateLayout();
                Assert.Equal(new[] { 1, 2 }, today.ViewModel.PersonalBroadcasts.Select(entry => entry.Anime.ID).Order());
                var list = (ItemsRepeater)today.FindName("PersonalBroadcastList");
                var first = Assert.IsType<Button>(list.TryGetElement(0));
                var second = Assert.IsType<Button>(list.TryGetElement(1));
                var offset = second.TransformToVisual(first).TransformPoint(new Windows.Foundation.Point());
                Assert.Equal(twoColumns, offset.X > 0);
                Assert.Equal(!twoColumns, offset.Y > 0);
                Assert.True(first.ActualWidth >= 205);
                Assert.Equal(2, Assert.Single(VisualChildren<TextBlock>(first), title => title.Text.StartsWith("作品", StringComparison.Ordinal)).MaxLines);
            }
            finally { await DetachAsync(root, today); await today.PendingActivation; await today.ViewModel.Theme.PendingLoad; }
        });

    private static Anime VisualItem(int id) => Item(id) with
    {
        Title = (id % 3) switch { 1 => "星海中的远行", 2 => "风吹过夏天", _ => "山间的小小旅馆" },
        CoverURL = $"https://img.animeido.com/uno-fixture/{id}.jpg",
        Description = "沿着星光出发，和伙伴一起走过遥远的城镇。每一段旅程，都留下一点新的发现。",
    };

    private static IEnumerable<T> VisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) yield return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var child in VisualChildren<T>(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }

    private static void SeedCoverImages()
    {
        foreach (var id in Enumerable.Range(1, 15))
        {
            var path = ImageCacheHelper.GetLocalPath(id);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var bitmap = new SKBitmap(180, 270);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColor.Parse(id % 2 == 0 ? "#235264" : "#363954"));
            using var paint = new SKPaint { Color = SKColor.Parse("#E4C57F"), IsAntialias = true };
            canvas.DrawCircle(128, 65, 26, paint);
            paint.Color = SKColor.Parse("#91B8B3");
            canvas.DrawRect(0, 170, 180, 100, paint);
            paint.Color = SKColor.Parse("#F0E5CE");
            canvas.DrawRect(35, 135, 58, 85, paint);
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 90);
            using var file = File.Create(path);
            encoded.SaveTo(file);
        }
    }

    private static async Task RenderAsync(FrameworkElement page, string name)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(page);
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        Assert.True(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0);
        Assert.Contains(pixels, value => value != 0);
        if (Environment.GetEnvironmentVariable("ANIMEIDO_TEST_CAPTURE_DIR") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        using var image = SKImage.FromPixelCopy(new SKImageInfo(bitmap.PixelWidth, bitmap.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul), pixels);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(Path.Combine(directory, name + ".png"));
        png.SaveTo(file);
    }
}
