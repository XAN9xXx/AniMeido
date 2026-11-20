using AniMeido.App.Services;
using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.Views;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using System.Diagnostics;
using System.Reflection;
using Windows.Foundation;
using Xunit;
using Xunit.Abstractions;
using static AniMeido.Desktop.Tests.DesktopTodayRecommendationPageTests;

namespace AniMeido.Desktop.Tests;

// 与日常界面测试共用进程时，框架可能已加载字体，无法取得冷启动样本。
public sealed class ExplicitColdFontDiagnosticAttribute : FactAttribute
{
    public ExplicitColdFontDiagnosticAttribute()
    {
        if (Environment.GetEnvironmentVariable("ANIMEIDO_COLD_FONT_DIAGNOSTIC") != "1")
            Skip = "请在新测试进程中显式启用冷字体诊断。";
    }
}

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopPageRetentionDiagnostics(ITestOutputHelper output)
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [ExplicitColdFontDiagnostic]
    [Trait("Category", "PerformanceDiagnostic")]
    [Trait("Category", "Mechanism")]
    public Task BundledReadingFontFacesMeasureColdLoadAndRepeatedSizeReuse()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var provider = DesktopFontProvider.Shared;
            var files = typeof(DesktopFontProvider).GetField("_files", Flags)!.GetValue(provider)!;
            Assert.False((bool)files.GetType().GetProperty("IsValueCreated")!.GetValue(files)!);
            FontSample("cold-shell");
            try
            {
                provider.UseSerif = true;
                foreach (var size in new[] { 17f, 18f, 24f })
                {
                    var font = await provider.MatchCharacterAsync('番', new Windows.UI.Text.FontWeight { Weight = 400 },
                        Windows.UI.Text.FontStretch.Normal, Windows.UI.Text.FontStyle.Normal, size);
                    Assert.NotNull(font);
                    FontSample($"first-size-{size}");
                }
                for (var repeat = 0; repeat < 500; repeat++)
                    foreach (var size in new[] { 17f, 18f, 24f })
                        Assert.NotNull(await provider.MatchCharacterAsync('番', new Windows.UI.Text.FontWeight { Weight = 400 },
                            Windows.UI.Text.FontStretch.Normal, Windows.UI.Text.FontStyle.Normal, size));
                FontSample("1500-repeated-requests");
                var panel = new StackPanel { Width = 600, Height = 160, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                foreach (var size in new[] { 17d, 18d, 24d })
                    panel.Children.Add(new TextBlock
                    {
                        Text = "番剧库 今天 推荐 作品详情 March 8.36",
                        FontFamily = new FontFamily(DesktopAppearanceFont.ForStyle(DesktopFontStyle.Serif).Family),
                        FontSize = size,
                    });
                root.Children.Add(panel);
                root.UpdateLayout();
                await Task.Delay(200);
                var rendered = new RenderTargetBitmap();
                await rendered.RenderAsync(panel);
                Assert.True(rendered.PixelWidth > 0);
                FontSample("first-common-text-render");
                for (var repeat = 0; repeat < 20; repeat++) await rendered.RenderAsync(panel);
                FontSample("20-same-text-renders");
                root.Children.Remove(panel);
                provider.UseSerif = false;
                await Task.Delay(1500);
                FontSample("serif-disabled-no-gc");
            }
            finally { provider.UseSerif = false; }
        });

    private void FontSample(string stage)
    {
        using var process = Process.GetCurrentProcess();
        var cache = (System.Collections.IDictionary)typeof(DesktopFontProvider).GetField("_chinese", Flags)!.GetValue(DesktopFontProvider.Shared)!;
        var sizes = new[] { "DesktopWenKai.ttf", "DesktopWenKaiReading.ttf" }
            .Select(name => $"{name}:{new FileInfo(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", name)).Length}");
        output.WriteLine($"font-files={string.Join(",", sizes)}");
        output.WriteLine($"font-stage={stage}; cachedSizes={cache.Count}; managedMiB={GC.GetTotalMemory(false) / 1048576d:F2}; committedMiB={GC.GetGCMemoryInfo().TotalCommittedBytes / 1048576d:F2}; privateMiB={process.PrivateMemorySize64 / 1048576d:F2}; workingMiB={process.WorkingSet64 / 1048576d:F2}; allocatedMiB={GC.GetTotalAllocatedBytes(false) / 1048576d:F2}; gen2={GC.CollectionCount(2)}; skiaFontBytes={SKGraphics.GetFontCacheUsed()}; skiaResourceBytes={SKGraphics.GetResourceCacheTotalBytesUsed()}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "PerformanceDiagnostic")]
    [Trait("Category", "Mechanism")]
    public Task RealLibrarySeasonsAndNavigationReleaseOffscreenPicturesAndTopLevelOwners(bool detailed)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            using var source = new CatalogSource();
            using var client = new HttpClient(new RatingHandler()) { BaseAddress = new Uri("https://api.animeido.com:38443") };
            var cache = new CacheService(fixture.Factory);
            var ratings = (MetadataDataSource)Activator.CreateInstance(typeof(MetadataDataSource), Flags, null,
                [NullLogger<MetadataDataSource>.Instance, client, cache, null], null)!;
            var tracking = new TrackingService(fixture.Factory);
            NavigationService? navigation = null;
            var services = new ServiceCollection()
                .AddSingleton<IAnimeDataSource>(source).AddSingleton(cache).AddSingleton(tracking)
                .AddSingleton(new DragDropService(tracking, new PersonalFeatureStateStore(fixture.Factory), NullLogger<DragDropService>.Instance))
                .AddSingleton(new SavedTagService(fixture.Factory)).AddSingleton(new BrowseHistoryService(fixture.Factory))
                .AddSingleton(new ArchiveService(fixture.Factory)).AddSingleton(ratings)
                .AddSingleton<IPluginNavigator>(_ => navigation!)
                .AddSingleton<Microsoft.Extensions.Logging.ILogger<PastSeasonPage>>(NullLogger<PastSeasonPage>.Instance)
                .AddSingleton<Microsoft.Extensions.Logging.ILogger<AnimeDetailPage>>(NullLogger<AnimeDetailPage>.Instance);
            using var provider = services.BuildServiceProvider();
            navigation = new(new PageFactory(provider), NullLogger<NavigationService>.Instance);
            var frame = new Frame { Width = 960, Height = 720 };
            navigation.Initialize(frame);
            root.Children.Add(frame);
            var previous = DesktopPresentationPreferences.UseDetailedCatalogCards;
            DesktopPresentationPreferences.Apply(detailed, DesktopPresentationPreferences.ShowPlanReminders);
            var trace = new ResourceTrace(output, detailed);
            try
            {
                await PumpAsync(root, trace, 1200);
                trace.Sample("empty-frame", root, navigation);
                await ExerciseLibraryAsync(root, frame, navigation, trace);
                await PumpAsync(root, trace, 1600);
                var cleared = trace.Sample("top-level-cleared", root, navigation);
                Assert.IsType<EmptyRoute>(frame.Content);
                Assert.Equal(0, StackCount(navigation));
                Assert.Equal(0, cleared.RootSubscriptions);
                Assert.Equal(0, cleared.PreferenceSubscriptions);
                Assert.Equal(0, cleared.DecodedBytes);
                Assert.Equal(0, cleared.BitmapSubscriptions);
                trace.AssertUnloadedPageOwnersCleared();
                Assert.Empty(trace.StateErrors);
            }
            finally
            {
                navigation.Stop();
                root.Children.Remove(frame);
                DesktopPresentationPreferences.Apply(previous, DesktopPresentationPreferences.ShowPlanReminders);
                await PumpAsync(root, trace, 1200);
            }
        });

    private static async Task ExerciseLibraryAsync(Grid root, Frame frame, NavigationService navigation, ResourceTrace trace)
    {
        navigation.Navigate(typeof(PastSeasonPage), new PastSeasonTarget(2022, Season.Spring));
        var page = Assert.IsType<PastSeasonPage>(frame.Content);
        trace.Pages.Add(new(page));
        await ReadyAsync(root, trace, () => !page.ViewModel.IsLoading && page.ViewModel.LoadedAnime.Count == 60);
        await PumpAsync(root, trace, 1200);
        var small = trace.Sample("60-items-top", root, navigation);
        Assert.True(small.DecodedBytes > 0, "初始封面应实际解码，随后再比较季度数据量。");
        var scroll = (ScrollViewer)page.FindName("CatalogScroller");
        Assert.True(scroll.ScrollableHeight > 0);
        await page.OnNavigatedToAsync(new PastSeasonTarget(2023, Season.Spring));
        await ReadyAsync(root, trace, () => !page.ViewModel.IsLoading && page.ViewModel.LoadedAnime.Count == 600);
        await PumpAsync(root, trace, 1200);
        var large = trace.Sample("600-items-top", root, navigation);
        Assert.InRange(large.LoadedCards, 1, small.LoadedCards + 12);
        Assert.InRange(large.LoadedCards, 1, 100);
        for (var visit = 0; visit < 3; visit++)
        {
            scroll.ChangeView(null, scroll.ScrollableHeight * (visit == 0 ? .5 : 1), null, true);
            await PumpAsync(root, trace, 1200);
            trace.Sample($"600-scroll-{visit}", root, navigation);
            await page.OnNavigatedToAsync(new PastSeasonTarget(2024 + visit, Season.Spring));
            await ReadyAsync(root, trace, () => !page.ViewModel.IsLoading && page.ViewModel.Season?.Year == 2024 + visit);
            await PumpAsync(root, trace, 1200);
            trace.Sample($"season-{2024 + visit}", root, navigation);
        }
        page.Width = frame.Width = 640;
        await PumpAsync(root, trace, 1200);
        trace.Sample("600-items-width640", root, navigation);
        page.Width = frame.Width = 960;
        var query = (AutoSuggestBox)page.FindName("FilterBox");
        query.Text = "作品";
        var sort = (ComboBox)page.FindName("SortComboBox");
        sort.SelectedIndex = 2;
        await PumpAsync(root, trace, 300);
        scroll.ChangeView(null, 800, null, true);
        await PumpAsync(root, trace, 1200);
        var offset = scroll.VerticalOffset;
        Assert.True(offset > 0);
        trace.Sample("before-detail-navigation", root, navigation);
        for (var visit = 0; visit < 3; visit++)
        {
            navigation.Navigate(typeof(AnimeDetailPage), 20260000 + visit);
            var detail = Assert.IsType<AnimeDetailPage>(frame.Content);
            trace.Pages.Add(new(detail));
            await PumpAsync(root, trace, 300);
            await detail.PendingActivation.WaitAsync(TimeSpan.FromSeconds(5));
            var detailScroll = (ScrollViewer)detail.FindName("ContentScrollViewer");
            root.UpdateLayout();
            detailScroll.ChangeView(null, detailScroll.ScrollableHeight, null, true);
            await PumpAsync(root, trace, 1400);
            trace.Sample($"detail-{visit}-library-in-stack", root, navigation);
            Assert.Equal(1, StackCount(navigation));
            navigation.GoBack();
            await PumpAsync(root, trace, 1400);
            Assert.Same(page, frame.Content);
            Assert.Equal("作品", query.Text);
            Assert.Equal(2, sort.SelectedIndex);
            trace.CheckOffset(visit, offset, scroll.VerticalOffset,
                (double)typeof(PastSeasonPage).GetField("_scrollOffset", Flags)!.GetValue(page)!);
            trace.Sample($"back-{visit}", root, navigation);
        }
        navigation.Navigate(typeof(AnimeDetailPage), 20260005);
        trace.Pages.Add(new(Assert.IsType<AnimeDetailPage>(frame.Content)));
        await PumpAsync(root, trace, 1400);
        trace.Sample("before-top-level-clear", root, navigation);
        navigation.NavigateTopLevel(typeof(EmptyRoute));
    }

    public sealed class EmptyRoute : Page { }

    private static int StackCount(NavigationService navigation)
        => ((NavigationStack)typeof(NavigationService).GetField("_backStack", Flags)!.GetValue(navigation)!).Count;

    private static async Task PumpAsync(Grid root, ResourceTrace trace, int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds)
        {
            root.UpdateLayout();
            trace.Observe(root);
            await Task.Delay(16);
        }
    }

    private static async Task ReadyAsync(Grid root, ResourceTrace trace, Func<bool> ready)
    {
        var timer = Stopwatch.StartNew();
        while (!ready() && timer.ElapsedMilliseconds < 5000) await PumpAsync(root, trace, 32);
        Assert.True(ready(), "合成季度数据应完成加载。");
    }

    private static IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Children<T>(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }

    private static bool OnScreen(FrameworkElement item, Grid root)
    {
        if (!item.IsLoaded || item.ActualWidth <= 0 || item.ActualHeight <= 0) return false;
        for (DependencyObject? current = item; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement { Visibility: Visibility.Collapsed }) return false;
        var bounds = item.TransformToVisual(root).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
        var viewport = Children<ScrollViewer>(root).FirstOrDefault(scroll => scroll.Name == "CatalogScroller");
        var visible = viewport is null ? new Rect(0, 0, root.ActualWidth, root.ActualHeight)
            : viewport.TransformToVisual(root).TransformBounds(new Rect(0, 0, viewport.ActualWidth, viewport.ActualHeight));
        return bounds.Left < visible.Right && bounds.Right > visible.Left && bounds.Top < visible.Bottom && bounds.Bottom > visible.Top;
    }

    private sealed record SampleResult(int LoadedCards, int RootSubscriptions, int PreferenceSubscriptions, long DecodedBytes, int BitmapSubscriptions);

    // 全部历史对象用弱引用；采样过程只在当前调用内临时持有目标。
    private sealed class ResourceTrace(ITestOutputHelper output, bool detailed)
    {
        internal List<WeakReference<Page>> Pages { get; } = [];
        internal List<string> StateErrors { get; } = [];
        private readonly List<WeakReference<AnimeCard>> _cards = [];
        private readonly List<WeakReference<Image>> _images = [];
        private readonly List<WeakReference<BitmapSource>> _sources = [];
        private readonly List<WeakReference<object>> _surfaces = [];
        private readonly List<WeakReference<Anime>> _models = [];
        private readonly int _startGen2 = GC.CollectionCount(2);

        internal void CheckOffset(int visit, double expected, double actual, double saved)
        {
            var message = $"detailed={detailed}; return={visit}; expectedOffset={expected}; actualOffset={actual}; savedOffset={saved}";
            output.WriteLine(message);
            if (Math.Abs(expected - actual) > .1) StateErrors.Add(message);
        }

        internal void AssertUnloadedPageOwnersCleared()
        {
            foreach (var page in Live(Pages).OfType<PastSeasonPage>())
            {
                Assert.False((bool)typeof(PastSeasonPage).GetField("_active", Flags)!.GetValue(page)!);
                foreach (var field in new[] { "_loading", "_dropHost", "_observedEntries" })
                    Assert.Null(typeof(PastSeasonPage).GetField(field, Flags)!.GetValue(page));
                Assert.Empty((System.Collections.IEnumerable)typeof(PastSeasonPage).GetField("_realizedCards", Flags)!.GetValue(page)!);
            }
        }

        internal void Observe(Grid root)
        {
            foreach (var page in Children<Page>(root)) Add(Pages, page);
            foreach (var card in Children<AnimeCard>(root)) { Add(_cards, card); if (card.Anime is { } anime) Add(_models, anime); }
            foreach (var image in Children<Image>(root)) Add(_images, image);
            foreach (var image in Live(_images))
                if (image.Source is BitmapImage { DecodePixelWidth: > 0 } bitmap)
                {
                    Add(_sources, bitmap);
                    if (Surface(bitmap) is { } surface) Add(_surfaces, surface);
                }
        }

        internal SampleResult Sample(string name, Grid root, NavigationService navigation)
        {
            Observe(root);
            var cards = Live(_cards).ToArray();
            var images = Live(_images).ToArray();
            var sources = Live(_sources).ToArray();
            var surfaces = Live(_surfaces).Where(surface => Bytes(surface) > 0).ToArray();
            var rootSubscriptions = (typeof(XamlRoot).GetField("Changed", Flags)!.GetValue(root.XamlRoot) as Delegate)?.GetInvocationList()
                .Count(handler => handler.Target?.GetType().DeclaringType == typeof(ManagedImageLoader)) ?? 0;
            var pageEvents = StaticSubscriptions("Changed", typeof(PastSeasonPage))
                + StaticSubscriptions("AppearanceChanged", typeof(AnimeDetailPage));
            var cardEvents = StaticSubscriptions("AppearanceChanged", typeof(AnimeCard));
            var subscriptions = sources.Sum(source =>
            {
                var list = typeof(ImageSource).GetField("_subscriptions", Flags)!.GetValue(source)!;
                return (int)list.GetType().GetProperty("Count", Flags)!.GetValue(list)!;
            });
            var visible = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var image in images.Where(image => OnScreen(image, root)))
                if (image.Source is BitmapImage { DecodePixelWidth: > 0 } bitmap && Surface(bitmap) is { } surface) visible.Add(surface);
            var loading = sources.Count(source => Bytes(Surface(source)) == 0 && images.Any(image => image.IsLoaded && ReferenceEquals(image.Source, source)));
            var states = images.Select(LoaderState).Where(state => state is not null).Cast<object>().ToArray();
            var pendingRequests = states.Count(state => state.GetType().GetField("_cancellation", Flags)!.GetValue(state) is not null);
            var roles = states.GroupBy(state => state.GetType().GetField("_request", Flags)!.GetValue(state) is { } request
                ? request.GetType().GetProperty("Kind")!.GetValue(request)!.ToString() : "None")
                .Select(group => $"{group.Key}:{group.Count()}");
            using var process = Process.GetCurrentProcess();
            var library = Children<PastSeasonPage>(root).FirstOrDefault();
            var currentRows = Children<ItemsRepeater>(root).Where(item => item.Name == "CatalogRows")
                .Sum(item => item.ItemsSource is System.Collections.IEnumerable values ? values.Cast<object>().Count() : 0);
            var catalogScroll = library?.FindName("CatalogScroller") as ScrollViewer;
            var firstCard = catalogScroll is null ? null : cards.Where(card => OnScreen(card, root))
                .OrderBy(card => card.TransformToVisual(catalogScroll).TransformPoint(new Point()).Y)
                .ThenBy(card => card.TransformToVisual(catalogScroll).TransformPoint(new Point()).X).FirstOrDefault();
            if (catalogScroll is not null)
                output.WriteLine($"anchor-stage={name}; offset={catalogScroll.VerticalOffset}; viewport={catalogScroll.ViewportWidth}x{catalogScroll.ViewportHeight}; extent={catalogScroll.ExtentWidth}x{catalogScroll.ExtentHeight}; firstId={firstCard?.Anime?.ID}; firstTop={firstCard?.TransformToVisual(catalogScroll).TransformPoint(new Point()).Y}; cacheLength={((ItemsRepeater)library!.FindName("CatalogRows")).VerticalCacheLength}");
            var retained = Live(Pages).OfType<PastSeasonPage>().Select(page => $"loaded:{page.IsLoaded}/entries:{page.ViewModel.Entries.Count}/models:{page.ViewModel.LoadedAnime.Count}");
            var caches = typeof(SKGraphics).GetMethods(BindingFlags.Static | BindingFlags.Public)
                .Where(method => method.Name.StartsWith("Get", StringComparison.Ordinal) && method.Name.Contains("Cache", StringComparison.Ordinal)
                    && method.GetParameters().Length == 0)
                .Select(method => $"{method.Name}={method.Invoke(null, null)}");
            output.WriteLine($"skia-stage={name}; {string.Join(";", caches)}");
            output.WriteLine($"detailed={detailed}; stage={name}; viewport={root.ActualWidth}x{root.ActualHeight}; stack={StackCount(navigation)}; frame={navigation.CurrentPageType?.Name}; weakPages={Live(Pages).Count()}/{Pages.Count}; weakLibrary=[{string.Join(",", retained)}]; models={Live(_models).Count()}/{_models.Count}; observedCards={_cards.Count}; liveCards={cards.Length}; loadedCards={cards.Count(card => card.IsLoaded)}; visibleCards={cards.Count(card => OnScreen(card, root))}; currentRows={currentRows}; entries={library?.ViewModel.Entries.Count ?? 0}; sources={sources.Length}/{_sources.Count}; decodedSurfaces={surfaces.Length}; decodedBytes={surfaces.Sum(Bytes)}; visibleBytes={visible.Sum(Bytes)}; offscreenBytes={surfaces.Where(surface => !visible.Contains(surface)).Sum(Bytes)}; decodePending={loading}; requestPending={pendingRequests}; imageRoles=[{string.Join(",", roles)}]; bitmapSubscriptions={subscriptions}; rootManagedSubscriptions={rootSubscriptions}; pagePreferenceSubscriptions={pageEvents}; cardPreferenceSubscriptions={cardEvents}; heapMiB={GC.GetTotalMemory(false) / 1048576d:F1}; committedMiB={GC.GetGCMemoryInfo().TotalCommittedBytes / 1048576d:F1}; privateMiB={process.PrivateMemorySize64 / 1048576d:F1}; workingMiB={process.WorkingSet64 / 1048576d:F1}; gen2={GC.CollectionCount(2) - _startGen2}");
            return new(cards.Count(card => card.IsLoaded), rootSubscriptions, pageEvents + cardEvents, surfaces.Sum(Bytes), subscriptions);
        }

        private static int StaticSubscriptions(string name, Type owner)
            => (typeof(DesktopPresentationPreferences).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) as Delegate)?
                .GetInvocationList().Count(handler => owner.IsInstanceOfType(handler.Target)) ?? 0;
        private static object? LoaderState(Image image)
        {
            var states = typeof(ManagedImageLoader).GetField("States", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            object?[] arguments = [image, null];
            return (bool)states.GetType().GetMethod("TryGetValue")!.Invoke(states, arguments)! ? arguments[1] : null;
        }
        private static void Add<T>(List<WeakReference<T>> references, T value) where T : class
        {
            if (!references.Any(reference => reference.TryGetTarget(out var known) && ReferenceEquals(known, value))) references.Add(new(value));
        }
        private static IEnumerable<T> Live<T>(List<WeakReference<T>> references) where T : class
        {
            foreach (var reference in references) if (reference.TryGetTarget(out var value)) yield return value;
        }
        private static object? Surface(BitmapSource source)
        {
            var data = typeof(ImageSource).GetField("_imageData", Flags)!.GetValue(source)!;
            return data.GetType().GetProperty("CompositionSurface", Flags)!.GetValue(data);
        }
        private static long Bytes(object? surface) => surface is null ? 0 : (long)surface.GetType().GetProperty("DecodedByteCount", Flags)!.GetValue(surface)!;
    }

    private sealed class CatalogSource : IAnimeDataSource, IDisposable
    {
        private readonly List<string> _paths = [];
        private readonly byte[] _jpeg;
        internal CatalogSource()
        {
            using var pixels = new SKBitmap(1200, 1800);
            pixels.Erase(SKColors.DarkBlue);
            using var encoded = pixels.Encode(SKEncodedImageFormat.Jpeg, 85);
            _jpeg = encoded.ToArray();
        }
        private Anime Item(int id) => new(id, $"作品{id}", null, [], new DateOnly(2020, 4, 1), $"https://img.animeido.com/diagnostic/{id}.jpg",
            "真实模板的合成简介", 2020, 4, Score: 8.7, MediaFormat: AnimeMediaFormat.Television);
        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct)
        {
            var items = Enumerable.Range(0, year == 2022 ? 60 : 600).Select(index => Item(year * 10000 + index)).ToList();
            foreach (var item in items)
            {
                var path = ImageCacheHelper.GetLocalPath(item.ID);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, _jpeg);
                _paths.Add(path);
            }
            return Task.FromResult(items);
        }
        public Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct) => Task.FromResult<Anime?>(Item(animeID));
        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct) => Task.FromResult(new List<Anime>());
        public Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct) => Task.FromResult(new List<Studio>());
        public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct) => Task.FromResult(new List<Tag>());
        public Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct) => Task.FromResult(new List<VoiceActor>());
        public Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct)
        {
            var roles = Enumerable.Range(0, 2).Select(index => new CharacterRole(index, $"角色{index}", null,
                $"https://img.animeido.com/diagnostic/character-{animeID}-{index}.jpg", [])).ToList();
            foreach (var role in roles)
            {
                var path = ImageCacheHelper.GetAvatarLocalPath(role.CharacterImage!);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, _jpeg);
                _paths.Add(path);
            }
            return Task.FromResult(roles);
        }
        public Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct) => Task.FromResult(new List<PersonWork>());
        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort, CancellationToken ct, string? airDateFrom = null, string? airDateTo = null) => Task.FromResult((new List<Anime>(), 0));
        public Task<(List<Anime> Results, int Total, int NextOffset)> SearchByKeywordAsync(string keyword, int offset, CancellationToken ct) => Task.FromResult((new List<Anime>(), 0, offset));
        public void Dispose() { foreach (var path in _paths) File.Delete(path); }
    }

    private sealed class RatingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"dataVersion":"diagnostic-v1","data":{"id":1,"firstDate":"2020-01-01","ratings":[]}}""", System.Text.Encoding.UTF8, "application/json"),
            };
            response.Headers.Add("X-Data-Version", "diagnostic-v1");
            return Task.FromResult(response);
        }
    }
}
