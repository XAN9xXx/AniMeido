using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.App.Services;
using AniMeido.Plugin.Base;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using AniMeido.Plugin.Base.Views;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Xunit;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopSearchPageTests
{
    [Theory]
    [InlineData(DesktopVisualStyle.Fluent10)]
    [InlineData(DesktopVisualStyle.Fluent11)]
    [InlineData(DesktopVisualStyle.Paper)]
    public Task SearchFormAndLandingHaveVisibleWidthAndTheButtonSubmits(DesktopVisualStyle visual)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var queries = new List<string>();
            await using var fixture = await SearchFixture.CreateAsync(new SearchSource
            {
                Search = (query, _, _) => { queries.Add(query); return Task.FromResult((new List<Anime> { Item(1, "合成作品") }, 1, 1)); },
            });
            var settings = new DesktopSettingsStore(fixture);
            await settings.SaveAsync(new DesktopSettings { VisualStyle = visual.ToString(), ThemeSetting = "Dark", FontStyle = "Serif" });
            await using var theme = new ThemeService(settings);
            await theme.InitializeThemeAsync(root);
            var page = fixture.SearchPage();
            try
            {
                await AttachAsync(root, page);
                await page.PendingActivation;
                root.UpdateLayout();
                await Task.Delay(30);
                var box = (AutoSuggestBox)page.FindName("SearchBox");
                Assert.True(box.ActualWidth > 200, $"搜索框宽度为 {box.ActualWidth}");
                Assert.True(Assert.Single(Children<TextBox>(box)).ActualWidth > 200);
                Assert.True(((FrameworkElement)((ScrollViewer)page.FindName("LandingScroll")).Content).ActualWidth > 200);
                box.Text = "合成作品";
                DesktopTodayRecommendationPageTests.Invoke(Children<Button>(page).Single(button => button.Content as string == "搜索"));
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (page.ViewModel.Results.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
                Assert.Equal("合成作品", Assert.Single(queries));
                Assert.Equal(1, Assert.Single(page.ViewModel.Results).Anime.ID);
                Assert.Equal(Visibility.Visible, ((Grid)page.FindName("SearchResultsLayout")).Visibility);
            }
            finally { await DetachAsync(root, page); await page.PendingActivation; }
        });

    [Fact]
    public Task ClearingTheActualSearchBoxRejectsTheLateReplyAndKeepsTheRecentQuery()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var reply = NewCompletion<(List<Anime>, int, int)>();
            var entered = NewCompletion();
            await using var fixture = await SearchFixture.CreateAsync(new SearchSource
            {
                Search = (_, _, _) => { entered.TrySetResult(); return reply.Task; },
            });
            var page = fixture.SearchPage();
            Task search = Task.CompletedTask;
            try
            {
                await AttachAsync(root, page);
                await page.PendingActivation;
                var box = (AutoSuggestBox)page.FindName("SearchBox");
                box.Text = "合成作品";
                search = page.SearchAsync(box.Text);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                box.Text = string.Empty;
                await Task.Delay(30); // Uno 在调度队列中发布 TextChanged。
                Assert.True(page.ViewModel.IsLanding);
                reply.TrySetResult(([Item(1, "迟到作品")], 1, 1));
                await search;
                Assert.True(page.ViewModel.IsLanding);
                Assert.Empty(page.ViewModel.Results);
                Assert.Contains("合成作品", page.ViewModel.RecentSearches);
                Assert.False(page.ViewModel.IsError);
            }
            finally
            {
                reply.TrySetResult(([], 0, 0));
                await search;
                await DetachAsync(root, page);
                await page.PendingActivation;
            }
        });

    [Fact]
    public Task SearchResultTemplateKeepsItsTypedAnimeAndMatchedName()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await SearchFixture.CreateAsync(new SearchSource());
            var page = fixture.SearchPage();
            var entry = new PastSeasonEntry(Item(7, "作品标题"), AnimeTrackingStatus.Following) { MatchedName = "匹配到的别名" };
            var presenter = new ContentPresenter
            {
                Content = entry, DataContext = entry,
                ContentTemplate = (DataTemplate)page.Resources["ResultCardTemplate"],
            };
            root.Children.Add(presenter);
            await Task.Delay(50);
            root.UpdateLayout();
            var card = Assert.Single(Children<AnimeCard>(presenter));
            Assert.Same(entry.Anime, card.Anime);
            Assert.Equal("作品标题", ((TextBlock)card.FindName("TitleText")).Text);
            Assert.Contains(Children<TextBlock>(presenter), text => text.Text == entry.MatchedName && text.ActualHeight > 0);
        });

    [Fact]
    public Task TagReturnKeepsTheSelectedYearsSortAndPage()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await SearchFixture.CreateAsync(new SearchSource());
            var queries = 0;
            var vm = fixture.TagViewModel((_, _, _, _) =>
            {
                ++queries;
                return Task.FromResult(Enumerable.Range(1, 25).Select(id => Item(id, $"作品 {id}")).ToList());
            });
            var page = fixture.TagPage(vm);
            try
            {
                await AttachAsync(root, page);
                await page.OnNavigatedToAsync("科幻");
                await page.PendingFavorite;
                ((ComboBox)page.FindName("SortSelector")).SelectedIndex = 1;
                vm.NextPage();
                Assert.Equal(20, vm.CurrentOffset);
                await DetachAsync(root, page);
                await page.OnNavigatedToAsync("科幻");
                await AttachAsync(root, page);
                await Task.WhenAll(page.PendingActivation, page.PendingFavorite);
                Assert.Equal(1, queries);
                Assert.Equal(20, vm.CurrentOffset);
                Assert.Equal(1, ((ComboBox)page.FindName("SortSelector")).SelectedIndex);
                Assert.Equal("2 / 2", ((TextBlock)page.FindName("PageInfo")).Text);
                Assert.False(((ProgressRing)page.FindName("LoadingRing")).IsActive);
            }
            finally { await DetachAsync(root, page); await Task.WhenAll(page.PendingActivation, page.PendingFavorite); }
        });

    [Fact]
    public Task TagFavoriteWaitsForTheActualWriteAndRejectsARepeatedClick()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await SearchFixture.CreateAsync(new SearchSource());
            var page = fixture.TagPage(fixture.TagViewModel((_, _, _, _) => Task.FromResult(new List<Anime>())));
            Task writing = Task.CompletedTask;
            try
            {
                await AttachAsync(root, page);
                await page.OnNavigatedToAsync("科幻");
                await page.PendingFavorite;
                using var connection = await fixture.Factory.OpenAsync();
                using var transaction = connection.BeginTransaction();
                var button = (Button)page.FindName("FavoriteTagButton");
                writing = page.ToggleFavoriteAsync();
                Assert.Same(writing, page.ToggleFavoriteAsync());
                Assert.False(button.IsEnabled);
                Assert.Equal("收藏标签", button.Content);
                transaction.Rollback();
                await writing.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(await fixture.SavedTags.IsTagSavedAsync("科幻"));
                Assert.Equal("已收藏", button.Content);
                Assert.True(button.IsEnabled);

                using var command = connection.CreateCommand();
                command.CommandText = "DROP TABLE saved_tags";
                await command.ExecuteNonQueryAsync();
                await page.ToggleFavoriteAsync();
                Assert.Equal("已收藏", button.Content);
                Assert.Equal("收藏未能更改，请重试。", ((TextBlock)page.FindName("FavoriteMessage")).Text);
                Assert.True(button.IsEnabled);
            }
            finally { await writing; await DetachAsync(root, page); }
        });

    [Fact]
    public Task SwitchingPersonRejectsTheOldReplyAndRendersTheNewYearGroup()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var oldReply = NewCompletion<List<PersonWork>>();
            var entered = NewCompletion();
            await using var fixture = await SearchFixture.CreateAsync(new SearchSource
            {
                Works = (id, _) =>
                {
                    if (id == 1) { entered.TrySetResult(); return oldReply.Task; }
                    return Task.FromResult(new List<PersonWork> { new(7, "作品 7", "配音", AirDate: new DateOnly(2026, 7, 1)) });
                },
            });
            var page = fixture.PersonPage();
            Task old = Task.CompletedTask;
            try
            {
                await AttachAsync(root, page);
                old = page.OnNavigatedToAsync((1, "旧人物"));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await page.OnNavigatedToAsync((2, "新人物"));
                oldReply.TrySetResult([new(1, "旧作品", "配音")]);
                await old;
                await Task.Delay(50);
                root.UpdateLayout();
                Assert.Equal("声优作品：新人物", ((TextBlock)page.FindName("TitleBlock")).Text);
                Assert.Equal(7, Assert.Single(page.ViewModel.Results).ID);
                var card = Assert.Single(Children<AnimeCard>(page));
                Assert.Equal(7, card.Anime!.ID);
                Assert.Equal(card.Anime.Title, ((TextBlock)card.FindName("TitleText")).Text);
                Assert.Contains(Children<TextBlock>(page), text => text.Text == "2026");
                Assert.Contains(Children<TextBlock>(page), text => text.Text == "配音");
            }
            finally { oldReply.TrySetResult([]); await old; await DetachAsync(root, page); await page.PendingActivation; }
        });

    [Fact]
    public void PersonRowsKeepEveryWorkAndUseSnapshotDatesOnlyForMissingDates()
    {
        var anime = new[] { Item(1, "一"), Item(2, "二"), Item(3, "三") with { AirDate = null }, Item(4, "四") with { AirDate = null } };
        var rows = DesktopPersonRow.Create(anime,
            [new(1, "一", "原作", AirDate: new DateOnly(2000, 1, 1)), new(3, "三", "配音", AirDate: new DateOnly(2025, 1, 1))], 1);
        Assert.Equal(new string?[] { "2026", null, "2025", "日期未知" }, rows.Select(row => row.YearLabel));
        Assert.Equal(new[] { 1, 2, 3, 4 }, rows.SelectMany(row => row.Works).Select(work => work.Anime.ID));
        Assert.Equal("原作", rows[0].Works[0].Role);
        Assert.Equal("配音", rows[2].Works[0].Role);
    }

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<T> NewCompletion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Anime Item(int id, string title) => new(id, title, null, [], new DateOnly(2026, 7, 1), null,
        "合成详情", 2026, 7, Score: 8, MediaFormat: AnimeMediaFormat.Television);

    private static async Task AttachAsync(Grid root, Page page)
    {
        var ready = NewCompletion();
        void Loaded(object sender, RoutedEventArgs args) => ready.TrySetResult();
        page.Loaded += Loaded;
        try { root.Children.Add(page); await ready.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { page.Loaded -= Loaded; }
    }
    private static async Task DetachAsync(Grid root, Page page)
    {
        if (!root.Children.Contains(page)) return;
        var ready = NewCompletion();
        void Unloaded(object sender, RoutedEventArgs args) => ready.TrySetResult();
        page.Unloaded += Unloaded;
        try { root.Children.Remove(page); await ready.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { page.Unloaded -= Unloaded; }
    }
    private static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) yield return value;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); ++index)
            foreach (var child in Children<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private sealed class SearchFixture : IAppDataPaths, IAsyncDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("AniMeido-search-ui-").FullName;
        private ServiceProvider _services = null!;
        internal SqliteConnectionFactory Factory { get; }
        internal SavedTagService SavedTags => _services.GetRequiredService<SavedTagService>();
        private SearchFixture() => Factory = new(this);
        public string DatabasePath => Path.Combine(_directory, "personal.db");
        public string BackupDirectory => Path.Combine(_directory, "backups");
        public string LogDirectory => Path.Combine(_directory, "logs");

        internal static async Task<SearchFixture> CreateAsync(SearchSource source)
        {
            var fixture = new SearchFixture();
            using var connection = await fixture.Factory.OpenAsync();
            await PersonalDatabaseSchema.CreateInitialBusinessTablesBeforeConfigAsync(connection);
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE config(Key TEXT PRIMARY KEY, Value TEXT NOT NULL)";
            await command.ExecuteNonQueryAsync();
            await PersonalDatabaseSchema.CreateInitialBusinessTablesAfterConfigAsync(connection);
            await PersonalDatabaseSchema.RunMigrationsAsync(connection);
            await PersonalDatabaseSchema.UpgradeEmptyDatabaseAsync(connection);
            var services = new ServiceCollection();
            services.AddLogging();
            await new BasePlugin().InitializeAsync(services);
            services.AddSingleton(fixture.Factory);
            services.AddSingleton<IAnimeDataSource>(source);
            fixture._services = services.BuildServiceProvider();
            return fixture;
        }
        internal GlobalSearchPage SearchPage() => new(_services.GetRequiredService<DragDropService>(),
            _services.GetRequiredService<IAnimeDataSource>(), _services.GetRequiredService<TrackingService>(),
            _services.GetRequiredService<PersonalFeatureStateStore>(), _services.GetRequiredService<LocalSearchService>(),
            SavedTags, new Navigator(), NullLogger<GlobalSearchPage>.Instance);
        internal TagSearchResultsViewModel TagViewModel(Func<string, int, int, CancellationToken, Task<List<Anime>>> load)
            => new(load, _services.GetRequiredService<TrackingService>().GetBlockedAnimeIdsAsync, NullLogger.Instance);
        internal TagSearchResultPage TagPage(TagSearchResultsViewModel vm)
            => new(vm, _services.GetRequiredService<DragDropService>(), SavedTags, new Navigator(), NullLogger<TagSearchResultPage>.Instance);
        internal PersonSearchResultPage PersonPage() => new(_services.GetRequiredService<IAnimeDataSource>(),
            _services.GetRequiredService<TrackingService>(), _services.GetRequiredService<DragDropService>(),
            new Navigator(), NullLogger<PersonSearchResultPage>.Instance);
        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await Factory.StopAsync();
            using var connection = Factory.CreateConnection();
            SqliteConnection.ClearPool(connection);
            Directory.Delete(_directory, recursive: true);
        }
    }
    private sealed class Navigator : IPluginNavigator
    {
        public void Navigate(Type pageType, object? parameter = null) { }
    }
    private sealed class SearchSource : IAnimeDataSource
    {
        internal Func<string, int, CancellationToken, Task<(List<Anime>, int, int)>> Search { get; init; }
            = (_, _, _) => Task.FromResult((new List<Anime>(), 0, 0));
        internal Func<int, CancellationToken, Task<List<PersonWork>>> Works { get; init; }
            = (_, _) => Task.FromResult(new List<PersonWork>());
        public Task<(List<Anime> Results, int Total, int NextOffset)> SearchByKeywordAsync(string keyword, int offset, CancellationToken ct) => Search(keyword, offset, ct);
        public Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct) => Works(personId, ct);
        public Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct) => Task.FromResult<Anime?>(Item(animeID, $"作品 {animeID}"));
        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct) => throw new NotSupportedException();
        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort, CancellationToken ct,
            string? airDateFrom = null, string? airDateTo = null) => throw new NotSupportedException();
    }
}
