using AniMeido.App.Services;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using AniMeido.Plugin.Base.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
            var action = new ActionCenterService(fixture.Factory);
            foreach (var id in Enumerable.Range(2, 5)) await action.UpsertPlanAsync(id, $"计划{id}", AnimePlanPriority.Normal, null, id);
            var today = fixture.Today(new Source { Schedule = (_, _) => Task.FromResult(new List<Anime> { VisualItem(1) }) });
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
                Assert.Equal(width < 944 ? 1 : 0, Grid.GetRow((Grid)today.FindName("PlanCard")));
                Assert.Equal(1, Assert.Single(today.ViewModel.PersonalBroadcasts).Anime.ID);
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
                var candidate = Assert.IsType<AnimeCard>(Assert.IsType<Border>(candidates.TryGetElement(0)).Child);
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

    private static Anime VisualItem(int id) => Item(id) with
    {
        Title = (id % 3) switch { 1 => "星海中的远行", 2 => "风吹过夏天", _ => "山间的小小旅馆" },
        CoverURL = $"https://img.animeido.com/uno-fixture/{id}.jpg",
        Description = "沿着星光出发，和伙伴一起走过遥远的城镇。每一段旅程，都留下一点新的发现。",
    };

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
