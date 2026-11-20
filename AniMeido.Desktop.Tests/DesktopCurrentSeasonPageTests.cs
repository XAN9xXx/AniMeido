using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using AniMeido.Plugin.Base.Views;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Xunit;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopCurrentSeasonPageTests
{
    [Fact]
    public Task ADetachedPageRejectsAnOldScheduleAndLoadsAgainOnReturn()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await CalendarFixture.CreateAsync();
            var oldSchedule = new TaskCompletionSource<List<Anime>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var source = new CalendarSource
            {
                ReadSchedule = call =>
                {
                    if (call != 1) return Task.FromResult(new List<Anime> { Item(2, "新作品", 7.5) });
                    entered.SetResult();
                    return oldSchedule.Task;
                },
            };
            var page = fixture.Page(source);
            Assert.Equal(0, source.ScheduleCalls);
            try
            {
                await AttachAsync(root, page);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var interrupted = page.PendingActivation;
                await DetachAsync(root, page);
                oldSchedule.TrySetResult([Item(1, "旧作品", 9)]);
                await interrupted;
                Assert.False(page.ViewModel.HasData);
                Assert.Empty(page.ViewModel.VisibleEntries);

                await AttachAsync(root, page);
                await page.PendingActivation;
                Assert.Equal(2, source.ScheduleCalls);
                Assert.Equal(2, Assert.Single(page.ViewModel.VisibleEntries).Anime.ID);
                Assert.False(((ProgressRing)page.FindName("LoadingRing")).IsActive);
            }
            finally
            {
                oldSchedule.TrySetResult([]);
                await fixture.StopPageAsync(root, page);
            }
        });

    [Fact]
    public Task SearchSortAndClearingSearchSwitchTheActualResultsSurface()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await CalendarFixture.CreateAsync();
            var source = new CalendarSource
            {
                ReadSchedule = _ => Task.FromResult(new List<Anime>
                {
                    Item(1, "作品甲", 7.5), Item(2, "作品乙", 8.5),
                }),
            };
            var page = fixture.Page(source);
            try
            {
                await AttachAsync(root, page);
                await page.PendingActivation;
                var filter = (TextBox)page.FindName("FilterBox");
                filter.Text = "作品";
                await Task.Delay(30); // 等待 TextChanged 更新结果区。
                Assert.True(page.ViewModel.IsFiltering);
                Assert.Equal(Visibility.Visible, ((ScrollViewer)page.FindName("ResultsScroller")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((Grid)page.FindName("DiscoverHost")).Visibility);
                ((ComboBox)page.FindName("SortBox")).SelectedIndex = 2;
                Assert.Equal(CalendarSort.Title, page.ViewModel.Sort);

                page.ViewModel.SelectDay(CalendarDay.OtherKey);
                Assert.Empty(filter.Text);
                Assert.False(page.ViewModel.IsFiltering);
                Assert.Equal(Visibility.Visible, ((Grid)page.FindName("ShelfHost")).Visibility);
                Assert.Equal(Visibility.Visible, ((Grid)page.FindName("DiscoverHost")).Visibility);
                Assert.False(((ComboBox)page.FindName("SortBox")).IsEnabled);
                Assert.Equal(1, source.ScheduleCalls);
            }
            finally { await fixture.StopPageAsync(root, page); }
        });

    [Fact]
    public Task RefreshUsesOneRequestAndPreservesTheSelectedDayAndSort()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await CalendarFixture.CreateAsync();
            var second = new TaskCompletionSource<List<Anime>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var source = new CalendarSource
            {
                ReadSchedule = call =>
                {
                    if (call == 1) return Task.FromResult(new List<Anime> { Item(1, "作品", 8) });
                    entered.SetResult();
                    return second.Task;
                },
            };
            var page = fixture.Page(source);
            try
            {
                await AttachAsync(root, page);
                await page.PendingActivation;
                page.ViewModel.SelectDay(2);
                page.ViewModel.Sort = CalendarSort.Title;
                var refresh = page.RefreshAsync();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await page.RefreshAsync();
                Assert.Equal(2, source.ScheduleCalls);
                second.SetResult([Item(2, "刷新后的作品", 9) with { Weekday = 2 }]);
                await refresh;
                Assert.Equal(2, page.ViewModel.SelectedWeekday);
                Assert.Equal(CalendarSort.Title, page.ViewModel.Sort);
                Assert.Equal(2, Assert.Single(page.ViewModel.VisibleEntries).Anime.ID);
            }
            finally
            {
                second.TrySetResult([]);
                await fixture.StopPageAsync(root, page);
            }
        });

    private static Anime Item(int id, string title, double score)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        return new(id, title, null, [], today.AddDays(-14), null, "合成验收简介", today.Year, today.Month,
            today.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)today.DayOfWeek,
            score, MediaFormat: AnimeMediaFormat.Television);
    }

    private static async Task AttachAsync(Grid root, Page page)
    {
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLoaded(object sender, RoutedEventArgs args) => loaded.TrySetResult();
        page.Loaded += OnLoaded;
        try { root.Children.Add(page); await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { page.Loaded -= OnLoaded; }
    }

    private static async Task DetachAsync(Grid root, Page page)
    {
        if (!root.Children.Contains(page)) return;
        var unloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnUnloaded(object sender, RoutedEventArgs args) => unloaded.TrySetResult();
        page.Unloaded += OnUnloaded;
        try { root.Children.Remove(page); await unloaded.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { page.Unloaded -= OnUnloaded; }
    }

    private sealed class CalendarFixture : IAppDataPaths, IAsyncDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("AniMeido-calendar-ui-").FullName;
        private SqliteConnectionFactory Factory { get; }
        private CalendarFixture() => Factory = new(this);
        public string DatabasePath => Path.Combine(_directory, "personal.db");
        public string BackupDirectory => Path.Combine(_directory, "backups");
        public string LogDirectory => Path.Combine(_directory, "logs");

        internal static async Task<CalendarFixture> CreateAsync()
        {
            var fixture = new CalendarFixture();
            using var connection = await fixture.Factory.OpenAsync();
            await PersonalDatabaseSchema.CreateInitialBusinessTablesBeforeConfigAsync(connection);
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE config(Key TEXT PRIMARY KEY, Value TEXT NOT NULL)";
            await command.ExecuteNonQueryAsync();
            await PersonalDatabaseSchema.CreateInitialBusinessTablesAfterConfigAsync(connection);
            await PersonalDatabaseSchema.RunMigrationsAsync(connection);
            await PersonalDatabaseSchema.UpgradeEmptyDatabaseAsync(connection);
            CurrentSeasonViewModel.ResetTimeMachineSession();
            return fixture;
        }

        internal CurrentSeasonPage Page(CalendarSource source)
        {
            var tracking = new TrackingService(Factory);
            var state = new PersonalFeatureStateStore(Factory);
            return new(source, new DragDropService(tracking, state, NullLogger<DragDropService>.Instance),
                tracking, state, new Navigator(), NullLogger<CurrentSeasonPage>.Instance);
        }

        internal async Task StopPageAsync(Grid root, CurrentSeasonPage page)
        {
            await DetachAsync(root, page);
            await page.PendingActivation;
            await Task.WhenAll(page.ViewModel.PendingOthersLoad, page.ViewModel.PendingTimeMachineLoad,
                page.ViewModel.LoadDailyPickDetailsCommand.ExecutionTask ?? Task.CompletedTask,
                page.ViewModel.NextDailyPickCommand.ExecutionTask ?? Task.CompletedTask,
                page.ViewModel.PendingTimeMachineShownSave);
        }

        public async ValueTask DisposeAsync()
        {
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

    private sealed class CalendarSource : IAnimeDataSource
    {
        internal int ScheduleCalls { get; private set; }
        internal required Func<int, Task<List<Anime>>> ReadSchedule { get; init; }
        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct) => ReadSchedule(++ScheduleCalls);
        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct) => Task.FromResult(new List<Anime>());
        public Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct) => Task.FromResult<Anime?>(Item(animeID, "详情", 7));
        public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct) => Task.FromResult(new List<Tag>());
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
