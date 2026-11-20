using System.Runtime.InteropServices.WindowsRuntime;
using AniMeido.App.Controls;
using AniMeido.App.Services;
using AniMeido.App.Themes;
using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using AniMeido.Plugin.Base.Views;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Xunit;
using Xunit.Abstractions;
using static AniMeido.Desktop.Tests.UnoVisualEvidence;
using static AniMeido.Desktop.Tests.DesktopTodayRecommendationPageTests;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopRouteFirstFrameTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public Task EnteringTheLibraryStartsCardsAtRestAndLeavesHoverAndFocusInteractive(bool slow, bool detailed)
        => RunRouteAsync(typeof(PastSeasonPage), slow, async (host, source) =>
        {
            var page = Assert.IsType<PastSeasonPage>(host.Frame.Content);
            var trace = new List<(double Width, bool Skeleton, double? CardX, bool Transition, bool Hover, bool Retry,
                bool CoverDecoded, double? CoverWidth, string Toolbar)>();
            var pixels = new List<CardPixels>();
            AnimeCard? card = null;
            var resources = Application.Current.Resources;
            for (var index = 0; index < 24; index++)
            {
                if (index == 15) source.Release();
                await Task.Delay(16);
                card ??= FirstCard(page);
                var surface = card?.FindName("CardSurface") as Border;
                trace.Add((host.Frame.ActualWidth, Find<ItemsRepeater>(page, "SkeletonRepeater").Visibility == Visibility.Visible,
                    card?.TransformToVisual(host.Root).TransformPoint(default).X, surface?.BackgroundTransition is not null,
                    surface?.Background is SolidColorBrush brush && brush.Color == ((SolidColorBrush)resources["DesktopCardHoverBrush"]).Color,
                    card?.FindName("RetryOverlay") is Grid { Visibility: Visibility.Visible },
                    card?.FindName("CoverImage") is Image { Source: BitmapImage { PixelWidth: > 0 } },
                    card?.FindName("CoverHost") is Grid cover ? cover.ActualWidth : null, ToolbarTrace(page, host)));
                if (card is not null && pixels.Count < 4)
                    pixels.Add(await ReadCardPixelsAsync(host.Root, card, detailed));
                await host.CaptureAsync("library", index);
            }
            source.Release();
            await WaitAsync(() => (card = FirstCard(page)) is not null);
            output.WriteLine(string.Join("\n", trace.Select((value, index) => $"{index:00} {value}")));
            output.WriteLine($"card raster: {string.Join(", ", pixels)}");
            Assert.Single(trace.Where(sample => sample.Width > 0).Select(sample => sample.Width).Distinct());
            Assert.DoesNotContain(trace, sample => sample.Hover);
            Assert.DoesNotContain(trace, sample => sample.Transition);
            Assert.NotEmpty(pixels);
            var normal = await ReadCardPixelsAsync(host.Root, card!, detailed);
            Assert.All(pixels, sample =>
            {
                Assert.Equal(normal.Surface, sample.Surface);
                Assert.Equal(normal.Edge, sample.Edge);
                Assert.Equal(normal.TitleMargin, sample.TitleMargin);
            });
            if (slow) Assert.Contains(trace, sample => sample.Skeleton);

            var poster = Assert.IsType<AnimeCard>(card);
            var posterSurface = Find<Border>(poster, "CardSurface");
            Assert.True(host.Button.IsChecked);
            host.Mouse.MoveTo(poster, 30, 70);
            await WaitAsync(() => posterSurface.Background is SolidColorBrush value
                && value.Color == ((SolidColorBrush)resources["DesktopCardHoverBrush"]).Color);
            var hoverColor = ((SolidColorBrush)resources["DesktopCardHoverBrush"]).Color;
            var hoverBrush = posterSurface.Background;
            for (var frame = 0; frame < 8; frame++)
            {
                if (frame == 2) host.Mouse.MoveTo(poster, 40, 90);
                if (frame == 4) host.Mouse.MoveTo(poster, 65, 60);
                var rendered = (await ReadCardPixelsAsync(host.Root, poster, detailed)).Surface;
                Assert.InRange((int)rendered.R, Math.Min(normal.Surface.R, hoverColor.R) - 1, Math.Max(normal.Surface.R, hoverColor.R) + 1);
                Assert.InRange((int)rendered.G, Math.Min(normal.Surface.G, hoverColor.G) - 1, Math.Max(normal.Surface.G, hoverColor.G) + 1);
                Assert.InRange((int)rendered.B, Math.Min(normal.Surface.B, hoverColor.B) - 1, Math.Max(normal.Surface.B, hoverColor.B) + 1);
                await Task.Delay(16);
            }
            poster.ShowMediaFormatBadge = !poster.ShowMediaFormatBadge;
            Assert.Same(hoverBrush, posterSurface.Background); // 信息更新时不重启同一个悬停效果
            var hovered = normal;
            await WaitAsync(async () => (hovered = await ReadCardPixelsAsync(host.Root, poster, detailed)).Surface != normal.Surface);
            Assert.True(hovered.Surface.R > normal.Surface.R);
            output.WriteLine($"normal={normal}; true mouse hover={hovered}");
            await host.CaptureAsync("library-hover", 0);
            host.Mouse.MoveTo(host.Button);
            Assert.True(poster.Focus(FocusState.Keyboard));
            Assert.Same(poster, FocusManager.GetFocusedElement(host.Root.XamlRoot!));
            await host.NavigateAwayAndBackAsync(page);
            for (var i = 0; i < 6; i++)
            {
                await Task.Delay(16);
                await host.CaptureAsync("library-return", i);
            }
            await WaitAsync(() => FirstCard(page) is not null);
            Assert.Null(Find<Border>(FirstCard(page)!, "CardSurface").BackgroundTransition);
        }, detailed);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task EnteringTheCalendarOnlyShowsTheArrangedLayoutAndRetainsItOnReturn(bool slow, bool wide)
        => RunRouteAsync(typeof(CurrentSeasonPage), slow, async (host, source) =>
        {
            var page = Assert.IsType<CurrentSeasonPage>(host.Frame.Content);
            var trace = new List<(bool Loading, bool BodyPainted, double Width, Point Toolbar, Point Mine, Point Discover, bool DailyPick, int Row)>();
            for (var index = 0; index < 24; index++)
            {
                if (index == 12) source.Release();
                await Task.Delay(16);
                trace.Add(Snapshot());
                await host.CaptureAsync("calendar", index);
            }
            source.Release();
            await page.PendingActivation;
            await WaitAsync(() => page.ViewModel.HasDailyPick && Find<Border>(page, "DiscoverPanel").ActualWidth > 0);
            var settled = Snapshot();
            output.WriteLine(string.Join("\n", trace.Select((value, index) => $"{index:00} {value}")));
            output.WriteLine($"settled {settled}");
            Assert.Single(trace.Where(sample => sample.Width > 0).Select(sample => sample.Width).Distinct());
            Assert.True(settled.BodyPainted);
            Assert.Equal(wide ? 0 : 1, settled.Row);
            if (wide) Assert.True(settled.Width > 1000 && settled.Width % 1 != 0);
            Assert.Contains(trace, frame => frame.BodyPainted);
            if (slow) Assert.Contains(trace, frame => frame.Loading);
            foreach (var frame in trace.Where(frame => frame.BodyPainted))
            {
                Assert.Equal(settled.DailyPick, frame.DailyPick);
                Assert.Equal(settled.Row, frame.Row);
                Assert.InRange(Math.Abs(settled.Discover.X - frame.Discover.X), 0, 1);
                Assert.InRange(Math.Abs(settled.Discover.Y - frame.Discover.Y), 0, 1);
            }
            foreach (var frame in trace.Where(frame => frame.Toolbar.Y > 0))
            {
                Assert.Equal(settled.Toolbar, frame.Toolbar);
                Assert.Equal(settled.Mine, frame.Mine);
            }

            var filter = Find<TextBox>(page, "FilterBox");
            Assert.True(filter.Focus(FocusState.Keyboard));
            Assert.Same(filter, FocusManager.GetFocusedElement(host.Root.XamlRoot!));
            await host.NavigateAwayAndBackAsync(page);
            for (var i = 0; i < 6; i++)
            {
                await Task.Delay(16);
                var returned = Snapshot();
                if (returned.BodyPainted) Assert.Equal(settled.Discover, returned.Discover);
                await host.CaptureAsync("calendar-return", i);
            }
            Assert.Equal(settled.Discover, Snapshot().Discover);

            (bool Loading, bool BodyPainted, double Width, Point Toolbar, Point Mine, Point Discover, bool DailyPick, int Row) Snapshot()
            {
                var overlay = Find<Grid>(page, "LoadingOverlay");
                var body = Find<ScrollViewer>(page, "BodyScroller");
                var discover = Find<Border>(page, "DiscoverPanel");
                var hasDailyPick = Find<Border>(page, "DailyPickPanel").Visibility == Visibility.Visible;
                var visibleBehindOverlay = overlay.Visibility == Visibility.Collapsed
                    || overlay.Background is not SolidColorBrush { Opacity: 1, Color.A: 255 };
                return (page.ViewModel.IsLoading, body.IsLoaded && body.Opacity > 0 && visibleBehindOverlay && discover.ActualWidth > 0,
                    host.Frame.ActualWidth, Find<Grid>(page, "ToolbarGrid").TransformToVisual(host.Root).TransformPoint(default),
                    Find<ToggleButton>(page, "MineScopeButton").TransformToVisual(host.Root).TransformPoint(default),
                    discover.TransformToVisual(host.Root).TransformPoint(default), hasDailyPick, Grid.GetRow(discover));
            }
        }, wide: wide);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task TheCalendarRevealsAnEmptySeasonAndRecoversWhenTheFirstRequestFails(bool failFirst)
        => RunRouteAsync(typeof(CurrentSeasonPage), false, async (host, source) =>
        {
            var page = Assert.IsType<CurrentSeasonPage>(host.Frame.Content);
            var overlay = Find<Grid>(page, "LoadingOverlay");
            var scroller = Find<ScrollViewer>(page, "BodyScroller");
            if (failFirst)
            {
                await WaitAsync(() => page.ViewModel.IsError && overlay.IsLoaded);
                Assert.Equal(0, scroller.Opacity);
                await host.CaptureAsync("calendar-error", 0);
                host.Mouse.Click(overlay);
                await WaitAsync(() => source.ScheduleReads == 2);
            }
            await WaitAsync(() => scroller.Opacity == 1);
            Assert.True(scroller.IsEnabled);
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            Assert.False(page.ViewModel.IsError);
            Assert.Equal(failFirst, page.ViewModel.HasDailyPick);
            if (!failFirst) Assert.Equal(Visibility.Collapsed, Find<Border>(page, "DailyPickPanel").Visibility);
            await host.CaptureAsync(failFirst ? "calendar-retry" : "calendar-empty", 0);
        }, calendarInput: failFirst ? CalendarInput.Retry : CalendarInput.Empty);

    private static Task RunRouteAsync(Type target, bool slow, Func<RouteHost, Source, Task> run,
        bool detailed = false, bool wide = false, CalendarInput calendarInput = CalendarInput.Data)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var store = new DesktopSettingsStore(fixture);
            await store.SaveAsync(new DesktopSettings { VisualStyle = "Paper", ThemeSetting = "Dark", FontStyle = "Serif",
                CatalogCardStyle = detailed ? "Detailed" : "Cover" });
            await using var theme = new ThemeService(store);
            await theme.InitializeThemeAsync(root);
            CurrentSeasonViewModel.ResetTimeMachineSession();
            using var source = new Source(slow, calendarInput);
            var tracking = new TrackingService(fixture.Factory);
            var state = new PersonalFeatureStateStore(fixture.Factory);
            var drag = new DragDropService(tracking, state, NullLogger<DragDropService>.Instance);
            var services = new ServiceCollection()
                .AddSingleton<IAnimeDataSource>(source)
                .AddSingleton(tracking).AddSingleton(state).AddSingleton(drag)
                .AddSingleton(new CacheService(fixture.Factory))
                .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
                .AddSingleton<PageFactory>().AddSingleton<NavigationService>()
                .AddSingleton<IPluginNavigator>(provider => provider.GetRequiredService<NavigationService>());
            await using var provider = services.BuildServiceProvider();
            var navigator = provider.GetRequiredService<NavigationService>();
            var route = new BasePlugin().GetNavigationItems().Single(item => item.PageType == target);
            var host = new RouteHost(root, navigator, route,
                (wide ? "wide-" : detailed ? "detailed-" : target == typeof(PastSeasonPage) ? "poster-" : "") + (slow ? "slow" : "cold"));
            if (wide)
            {
                UnoVisualEvidence.SetScale(root.XamlRoot!, 1.25f);
                host.Root.HorizontalAlignment = HorizontalAlignment.Left;
                host.Root.Width = 1283.2;
            }
            try
            {
                await WaitAsync(() => host.Button.ActualWidth > 0 && host.Button.IsLoaded);
                await host.CaptureAsync(target == typeof(PastSeasonPage) ? "library-before" : "calendar-before", 0);
                host.Mouse.Click(host.Button);
                Assert.Equal(target, navigator.CurrentPageType);
                await run(host, source);
            }
            finally
            {
                source.Release();
                if (host.Frame.Content is CurrentSeasonPage calendar)
                {
                    navigator.NavigateTopLevel(typeof(OtherRoute));
                    await WaitAsync(() => !calendar.IsLoaded);
                    await Task.WhenAll(calendar.PendingActivation, calendar.ViewModel.PendingOthersLoad,
                        calendar.ViewModel.PendingTimeMachineLoad, calendar.ViewModel.PendingTimeMachineShownSave);
                }
                else if (host.Frame.Content is PastSeasonPage library)
                {
                    navigator.NavigateTopLevel(typeof(OtherRoute));
                    await WaitAsync(() => !library.IsLoaded);
                }
                navigator.Stop();
                root.Children.Remove(host.Root);
                if (wide) UnoVisualEvidence.SetScale(root.XamlRoot!, 0);
            }
        });

    private static T Find<T>(FrameworkElement parent, string name) where T : FrameworkElement
        => Assert.IsAssignableFrom<T>(parent.FindName(name));

    private static AnimeCard? FirstCard(Page page) => Descendants(page).OfType<AnimeCard>()
        .FirstOrDefault(card => card.IsLoaded && card.ActualWidth > 0 && card.ActualHeight > 0);

    private static async Task WaitAsync(Func<bool> done)
    {
        for (var i = 0; i < 150 && !done(); ++i) await Task.Delay(16);
        Assert.True(done());
    }

    private static async Task WaitAsync(Func<Task<bool>> done)
    {
        for (var frame = 0; frame < 25; frame++)
        {
            if (await done()) return;
            await Task.Delay(16);
        }
        Assert.Fail("没有采到实际悬停后的颜色。");
    }

    private sealed record CardPixels(Windows.UI.Color Surface, Windows.UI.Color Cover, Windows.UI.Color Edge, Windows.UI.Color TitleMargin);

    private static async Task<CardPixels> ReadCardPixelsAsync(FrameworkElement root, AnimeCard card, bool detailed)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(root);
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        var surface = Find<Border>(card, "CardSurface");
        var cover = Find<Grid>(card, "CoverHost");
        var title = Find<FrameworkElement>(card, detailed ? "DetailedTitle" : "TitleText");
        return new(
            Pixel(surface, surface.ActualWidth - 10, surface.ActualHeight - 9),
            Pixel(cover, cover.ActualWidth / 2, cover.ActualHeight / 3),
            Pixel(cover, cover.ActualWidth / 2, 0),
            Pixel(title, title.ActualWidth - 2, 1));

        Windows.UI.Color Pixel(FrameworkElement element, double x, double y)
        {
            var pt = element.TransformToVisual(root).TransformPoint(new Point(x, y));
            var offset = 4 * ((int)pt.Y * bitmap.PixelWidth + (int)pt.X);
            return Windows.UI.Color.FromArgb(pixels[offset + 3], pixels[offset + 2], pixels[offset + 1], pixels[offset]);
        }
    }

    private static string ToolbarTrace(PastSeasonPage page, RouteHost host)
    {
        var resources = Application.Current.Resources;
        var hover = ((SolidColorBrush)resources["DesktopButtonHoverBrush"]).Color;
        var controls = new Control[] { Find<Button>(page, "PrevSeasonButton"), Find<Button>(page, "NextSeasonButton"),
            Find<ToggleSwitch>(page, "HideCompletedSwitch"), Find<ComboBox>(page, "SortComboBox") }
            .Concat(Descendants(page).OfType<ToggleButton>().Where(button => button.Tag is PastSeasonFormatChip));
        return $"nav={host.Button.IsChecked}:"
            + string.Join(";", controls.Select(control =>
                $"{control.Name}{(control.Tag is PastSeasonFormatChip chip ? $"{chip.Label}/{chip.Count}/{chip.IsSelected}" : "")}"
                + $"={control.IsEnabled}/{control.ActualWidth:F0}"
                + (Descendants(control).OfType<ContentPresenter>().FirstOrDefault(item => item.Name == "Surface") is { } surface
                    ? $"/hover:{surface.Background is SolidColorBrush brush && brush.Color == hover}" : "")));
    }

    private sealed class OtherRoute : Page
    {
        public OtherRoute() => Content = new TextBlock { Text = "始发页 / 详情占位" };
    }

    private sealed class RouteHost
    {
        private readonly NavigationService _navigator;
        private readonly string _kind;
        internal Grid Root { get; }
        internal Frame Frame { get; } = new();
        internal DesktopShellNavigationItem Button { get; }
        internal DesktopTestMouse Mouse { get; }

        internal RouteHost(Grid root, NavigationService nav, PluginNavigationItem route, string kind)
        {
            _navigator = nav;
            _kind = kind;
            var resources = Application.Current.Resources;
            var shell = new DesktopShellResources();
            Root = new Grid { Background = (Brush)resources["DesktopPageBrush"] };
            Root.RowDefinitions.Add(new() { Height = new(48) });
            Root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
            Root.ColumnDefinitions.Add(new() { Width = new(196) });
            Root.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            Grid.SetRow(Frame, 1);
            Grid.SetColumn(Frame, 1);
            _navigator.Initialize(Frame);
            _navigator.NavigateTopLevel(typeof(OtherRoute));
            var pane = new StackPanel { Background = (Brush)resources["DesktopNavigationBrush"], Padding = new(0, 8, 0, 8) };
            foreach (var item in new BasePlugin().GetNavigationItems().Where(item => !item.IsSettingsPage))
            {
                var button = new DesktopShellNavigationItem(item.Label, item.Icon)
                    { Style = (Style)shell["DesktopShellNavigationStyle"], Tag = item };
                button.ApplyAppearance(DesktopVisualStyle.Paper, true, resources);
                pane.Children.Add(button);
            }
            Button = pane.Children.OfType<DesktopShellNavigationItem>()
                .Single(button => (button.Tag as PluginNavigationItem)?.PageType == route.PageType);
            Button.Click += (_, _) => _navigator.NavigateTopLevel(route.PageType!);
            _navigator.Navigated += type =>
            {
                if (type == route.PageType) Button.IsChecked = true;
            };
            Grid.SetRow(pane, 1);
            Root.Children.Add(pane);
            Root.Children.Add(Frame);
            root.Children.Add(Root);
            Mouse = new DesktopTestMouse(root);
        }

        internal async Task NavigateAwayAndBackAsync(Page page)
        {
            _navigator.Navigate(typeof(OtherRoute));
            await WaitAsync(() => !page.IsLoaded);
            _navigator.GoBack();
            Assert.Same(page, Frame.Content);
        }

        internal Task CaptureAsync(string route, int frame)
            => UnoVisualEvidence.CaptureAsync(Root, "F-navigation", $"{route}-{_kind}-{frame:D2}");
    }

    private enum CalendarInput { Data, Empty, Retry }

    private sealed class Source(bool slow, CalendarInput calendarInput) : IAnimeDataSource, IDisposable
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly DesktopManagedImageLifetimeTests.CachedImage[] _covers = Enumerable.Range(0, 8)
            .Select(_ => new DesktopManagedImageLifetimeTests.CachedImage(false, 360, 540)).ToArray();
        internal int ScheduleReads { get; private set; }
        internal void Release() => _gate.TrySetResult();

        public void Dispose()
        {
            foreach (var cover in _covers) cover.Dispose();
        }

        public async Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct)
        {
            if (slow) await _gate.Task.WaitAsync(ct);
            return Items(year, (int)season * 3 - 2);
        }

        public async Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct)
        {
            if (slow) await _gate.Task.WaitAsync(ct);
            ScheduleReads++;
            if (calendarInput == CalendarInput.Empty) return [];
            if (calendarInput == CalendarInput.Retry && ScheduleReads == 1)
                throw new HttpRequestException("离屏测试：首次读取失败。");
            return Items(DateTime.Today.Year, DateTime.Today.Month);
        }

        private List<Anime> Items(int year, int month) => _covers.Select((cover, i) =>
            new Anime(cover.Id, $"合成作品 {i + 1}", null, [], new DateOnly(year, month, 1), cover.Url,
                "导航首帧的合成简介。", year, month, Score: 8.6, MediaFormat: AnimeMediaFormat.Television,
                Weekday: (int)DateTime.Today.DayOfWeek == 0 ? 7 : (int)DateTime.Today.DayOfWeek)).ToList();

        public Task<Anime?> GetAnimeDetailAsync(int id, CancellationToken ct)
            => Task.FromResult<Anime?>(Items(DateTime.Today.Year, DateTime.Today.Month).FirstOrDefault(item => item.ID == id));
        public Task<List<Tag>> GetTagsAsync(int id, CancellationToken ct) => Task.FromResult(new List<Tag>());
        public Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct) => throw new NotSupportedException();
        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort,
            CancellationToken ct, string? airDateFrom = null, string? airDateTo = null) => throw new NotSupportedException();
        public Task<(List<Anime> Results, int Total, int NextOffset)> SearchByKeywordAsync(string keyword, int offset,
            CancellationToken ct) => throw new NotSupportedException();
    }
}
