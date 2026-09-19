using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

/// <summary>今天页“今日主题”：按日期轮换、换一批、替补与重启后恢复。</summary>
public sealed class TodayThemeTests : DbTestBase
{
    private static readonly DateOnly Start = new(2026, 9, 19);

    // 候选在一次运行中缓存，每个测试从干净状态开始。
    public TodayThemeTests() => TodayThemeViewModel.ResetSessionCache();

    [Fact]
    public void Schedule_IsStableAndCoversEveryThemeEachCycle()
    {
        Assert.Equal(TodayThemeSchedule.For(Start), TodayThemeSchedule.For(Start));

        // 从任意一轮的第一天起，连续 9 天正好是 9 个不同的主题。
        var cycleStart = Start.AddDays(-TodayThemeSchedule.For(Start).Position);
        var themes = Enumerable.Range(0, TodayThemeSchedule.Count)
            .Select(offset => TodayThemeSchedule.For(cycleStart.AddDays(offset)).Kind)
            .ToList();
        Assert.Equal(Enum.GetValues<TodayThemeKind>().Order(), themes.Order());
    }

    [Fact]
    public void Schedule_NeverRepeatsOnConsecutiveDays()
    {
        var previous = TodayThemeSchedule.For(Start.AddDays(-1000)).Kind;
        for (var offset = -999; offset <= 1000; offset++)
        {
            var current = TodayThemeSchedule.For(Start.AddDays(offset)).Kind;
            Assert.NotEqual(previous, current);
            previous = current;
        }
    }

    [Fact]
    public void RestoreBatch_KeepsStoredItemsAndFillsFromTheirPosition()
    {
        int[] order = [10, 11, 12, 13, 14, 15, 16, 17];

        // 12 已被标记：保留 13、14，从 13 的位置往后补。
        var batch = TodayThemeViewModel.RestoreBatch(order, [12, 13, 14], 3, id => id != 12);

        Assert.Equal(new[] { 13, 14, 15 }, batch);
    }

    [Fact]
    public void NextBatch_ContinuesAfterCurrentAndWraps()
    {
        int[] order = [1, 2, 3, 4, 5];

        Assert.Equal(new[] { 1, 2 }, TodayThemeViewModel.NextBatch(order, [], 2, _ => true));
        Assert.Equal(new[] { 4, 5 }, TodayThemeViewModel.NextBatch(order, [2, 3], 2, _ => true));
        // 到末尾回到开头，跳过不能显示的。
        Assert.Equal(new[] { 1, 3 }, TodayThemeViewModel.NextBatch(order, [4, 5], 2, id => id != 2));
    }

    [Fact]
    public void PreviousSeason_WrapsFromWinterToLastFall()
    {
        Assert.Equal((2025, Season.Fall), TodayThemeService.PreviousSeason((2026, Season.Winter)));
        Assert.Equal((2026, Season.Spring), TodayThemeService.PreviousSeason((2026, Season.Summer)));
    }

    [Fact]
    public void RankUnmarked_SkipsMarkedUnscoredAndTodaysDailyPick()
    {
        var context = new TodayThemeContext(
            Start,
            new Dictionary<int, AnimeTrackingStatus>
            {
                [2] = AnimeTrackingStatus.Completed,
                [3] = AnimeTrackingStatus.Blocked,
            },
            ExcludedAnimeId: 4);
        Anime[] items = [Item(1, 7.0), Item(2, 9.0), Item(3, 8.0), Item(4, 8.5), Item(5, null), Item(6, 7.5)];

        var ranked = TodayThemeService.RankUnmarked(items, context);

        Assert.Equal(new[] { 6, 1 }, ranked.Select(anime => anime.ID));
    }

    [Fact]
    public async Task TodayThemeState_RoundTripsThroughConfig()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        Assert.Null(await tracking.LoadTodayThemeAsync());

        var state = new TodayThemeState(Start, TodayThemeKind.Movies, true, [3, 1, 2]);
        await tracking.SaveTodayThemeAsync(state);

        var loaded = await tracking.LoadTodayThemeAsync();
        Assert.NotNull(loaded);
        Assert.Equal(state.Date, loaded.Date);
        Assert.Equal(state.Theme, loaded.Theme);
        Assert.True(loaded.IsFallback);
        Assert.Equal(state.AnimeIds, loaded.AnimeIds);
    }

    [Fact]
    public async Task Load_ShowsSeasonThemeWithoutMarkedItems()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        await tracking.SetStatusAsync(101, AnimeTrackingStatus.Completed);
        var date = DateWith(TodayThemeKind.OneYearAgoSeason);
        var vm = new TodayThemeViewModel(new ThemeSource(), tracking);

        await vm.LoadForDateAsync(date);

        Assert.Equal("一年前的这一季", vm.Title);
        Assert.False(vm.IsFallback);
        Assert.Equal(TodayThemeViewModel.BatchSize, vm.Items.Count);
        Assert.DoesNotContain(vm.Items, item => item.Anime.ID == 101);
        Assert.True(vm.CanShowNextBatch);
    }

    [Fact]
    public async Task Load_UsesFallbackWhenTodaysThemeHasNoContent()
    {
        // “搁置最久的在看”在这一批还没有实现，按没有内容处理。
        await RunProductionMigrationAsync();
        var date = DateWith(TodayThemeKind.Stalled);
        var vm = new TodayThemeViewModel(new ThemeSource(), new TrackingService(DbFactory));

        await vm.LoadForDateAsync(date);

        Assert.True(vm.IsFallback);
        Assert.Equal("上一季的高分作品", vm.Title);
        Assert.Contains("搁置最久的在看", vm.Caption);
        Assert.NotEmpty(vm.Items);
    }

    [Fact]
    public async Task NextBatch_IsKeptAfterRestartOnTheSameDay()
    {
        await RunProductionMigrationAsync();
        var date = DateWith(TodayThemeKind.LastSeasonTop);
        var first = new TodayThemeViewModel(new ThemeSource(), new TrackingService(DbFactory));
        await first.LoadForDateAsync(date);
        var firstBatch = first.Items.Select(item => item.Anime.ID).ToList();

        await first.NextBatchCommand.ExecuteAsync(null);
        var secondBatch = first.Items.Select(item => item.Anime.ID).Order().ToList();
        Assert.Empty(secondBatch.Intersect(firstBatch));

        // 模拟重启：清掉运行中的缓存，新建 ViewModel 读同一个数据库。
        TodayThemeViewModel.ResetSessionCache();
        var restarted = new TodayThemeViewModel(new ThemeSource(), new TrackingService(DbFactory));
        await restarted.LoadForDateAsync(date);

        Assert.Equal(secondBatch, restarted.Items.Select(item => item.Anime.ID).Order());
    }

    [Fact]
    public async Task MarkedItemIsReplacedAfterRestartButKeptOnSameDayRefresh()
    {
        await RunProductionMigrationAsync();
        var date = DateWith(TodayThemeKind.LastSeasonTop);
        var tracking = new TrackingService(DbFactory);
        var vm = new TodayThemeViewModel(new ThemeSource(), tracking);
        await vm.LoadForDateAsync(date);
        var marked = vm.Items[0];

        await vm.ApplyStatusAsync(marked, AnimeTrackingStatus.PlanToWatch);
        Assert.Equal(AnimeTrackingStatus.PlanToWatch, marked.Status);

        // 同一天刷新（例如标记后页面随之刷新）：刚标记的还在，并显示状态。
        await vm.LoadForDateAsync(date);
        Assert.Contains(vm.Items, item => item.Anime.ID == marked.Anime.ID
            && item.Status == AnimeTrackingStatus.PlanToWatch);

        // 重启后：已标记的由后面的作品补上。
        var restarted = new TodayThemeViewModel(new ThemeSource(), tracking);
        await restarted.LoadForDateAsync(date);
        Assert.DoesNotContain(restarted.Items, item => item.Anime.ID == marked.Anime.ID);
        Assert.Equal(TodayThemeViewModel.BatchSize, restarted.Items.Count);
    }

    [Fact]
    public async Task Load_FailureShowsRetry()
    {
        await RunProductionMigrationAsync();
        var date = DateWith(TodayThemeKind.Movies);
        var vm = new TodayThemeViewModel(new ThemeSource { Fails = true }, new TrackingService(DbFactory));

        await vm.LoadForDateAsync(date);

        Assert.True(vm.IsFailed);
        Assert.False(vm.IsLoading);
        Assert.True(vm.HasStatusText);
    }

    private static DateOnly DateWith(TodayThemeKind kind)
    {
        var date = Start;
        while (TodayThemeSchedule.For(date).Kind != kind)
            date = date.AddDays(1);

        return date;
    }

    private static Anime Item(int id, double? score, AnimeMediaFormat format = AnimeMediaFormat.Television) => new(
        id,
        $"作品{id}",
        null,
        [],
        new DateOnly(2025, 7, 1),
        null,
        string.Empty,
        2025,
        7,
        Score: score,
        MediaFormat: format);

    private sealed class ThemeSource : IAnimeDataSource
    {
        public bool Fails { get; init; }

        private static T Unexpected<T>() => throw new NotSupportedException("Unexpected data-source request.");

        // 每一季 20 部带评分的作品。
        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct)
            => Fails
                ? Task.FromException<List<Anime>>(new HttpRequestException("offline"))
                : Task.FromResult(Enumerable.Range(101, 20).Select(id => Item(id, 6.0 + (id - 100) * 0.1)).ToList());

        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort, CancellationToken ct,
            string? airDateFrom = null, string? airDateTo = null)
            => Fails
                ? Task.FromException<(List<Anime>, int)>(new HttpRequestException("offline"))
                : Task.FromResult((Enumerable.Range(201, 10).Select(id => Item(id, 8.0, AnimeMediaFormat.Movie)).ToList(), 10));

        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct) => Unexpected<Task<List<Anime>>>();
        public Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct) => Unexpected<Task<Anime?>>();
        public Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct) => Unexpected<Task<List<Studio>>>();
        public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct) => Unexpected<Task<List<Tag>>>();
        public Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct) => Unexpected<Task<List<VoiceActor>>>();
        public Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct) => Unexpected<Task<List<CharacterRole>>>();
        public Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct) => Unexpected<Task<List<PersonWork>>>();
        public Task<(List<Anime> Results, int Total)> SearchByKeywordAsync(string keyword, int offset, CancellationToken ct)
            => Unexpected<Task<(List<Anime>, int)>>();
    }
}
