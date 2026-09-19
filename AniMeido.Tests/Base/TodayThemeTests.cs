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
        var vm = NewViewModel(new ThemeSource(), tracking);

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
        // 没有追番中或补番中的作品，“搁置最久的在看”没有内容。
        await RunProductionMigrationAsync();
        var date = DateWith(TodayThemeKind.Stalled);
        var vm = NewViewModel(new ThemeSource(), new TrackingService(DbFactory));

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
        var first = NewViewModel(new ThemeSource(), new TrackingService(DbFactory));
        await first.LoadForDateAsync(date);
        var firstBatch = first.Items.Select(item => item.Anime.ID).ToList();

        await first.NextBatchCommand.ExecuteAsync(null);
        var secondBatch = first.Items.Select(item => item.Anime.ID).Order().ToList();
        Assert.Empty(secondBatch.Intersect(firstBatch));

        // 模拟重启：清掉运行中的缓存，新建 ViewModel 读同一个数据库。
        TodayThemeViewModel.ResetSessionCache();
        var restarted = NewViewModel(new ThemeSource(), new TrackingService(DbFactory));
        await restarted.LoadForDateAsync(date);

        Assert.Equal(secondBatch, restarted.Items.Select(item => item.Anime.ID).Order());
    }

    [Fact]
    public async Task MarkedItemIsReplacedAfterRestartButKeptOnSameDayRefresh()
    {
        await RunProductionMigrationAsync();
        var date = DateWith(TodayThemeKind.LastSeasonTop);
        var tracking = new TrackingService(DbFactory);
        var vm = NewViewModel(new ThemeSource(), tracking);
        await vm.LoadForDateAsync(date);
        var marked = vm.Items[0];

        await vm.ApplyStatusAsync(marked, AnimeTrackingStatus.PlanToWatch);
        Assert.Equal(AnimeTrackingStatus.PlanToWatch, marked.Status);

        // 同一天刷新（例如标记后页面随之刷新）：刚标记的还在，并显示状态。
        await vm.LoadForDateAsync(date);
        Assert.Contains(vm.Items, item => item.Anime.ID == marked.Anime.ID
            && item.Status == AnimeTrackingStatus.PlanToWatch);

        // 重启后：已标记的由后面的作品补上。
        var restarted = NewViewModel(new ThemeSource(), tracking);
        await restarted.LoadForDateAsync(date);
        Assert.DoesNotContain(restarted.Items, item => item.Anime.ID == marked.Anime.ID);
        Assert.Equal(TodayThemeViewModel.BatchSize, restarted.Items.Count);
    }

    [Fact]
    public async Task Load_FailureShowsRetry()
    {
        await RunProductionMigrationAsync();
        var date = DateWith(TodayThemeKind.Movies);
        var vm = NewViewModel(new ThemeSource { Fails = true }, new TrackingService(DbFactory));

        await vm.LoadForDateAsync(date);

        Assert.True(vm.IsFailed);
        Assert.False(vm.IsLoading);
        Assert.True(vm.HasStatusText);
    }

    [Fact]
    public void SelectRevisited_KeepsUnmarkedViewedTwiceNewestFirst()
    {
        var context = new TodayThemeContext(
            Start,
            new Dictionary<int, AnimeTrackingStatus> { [3] = AnimeTrackingStatus.Following },
            ExcludedAnimeId: 4);
        var now = new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);
        (int, string?, DateTime, int)[] history =
        [
            (1, "a", now.AddDays(-3), 2),
            (2, "b", now.AddDays(-1), 5),
            (3, "c", now, 4),
            (4, "d", now, 3),
            (5, "e", now, 1),
        ];

        var picked = TodayThemeService.SelectRevisited(history, context);

        Assert.Equal(new[] { 2, 1 }, picked.Select(item => item.AnimeId));
    }

    [Fact]
    public void SelectUnrated_PutsFinishedFirstAndSkipsRated()
    {
        var context = new TodayThemeContext(
            Start,
            new Dictionary<int, AnimeTrackingStatus>
            {
                [1] = AnimeTrackingStatus.Watching,
                [2] = AnimeTrackingStatus.Completed,
                [3] = AnimeTrackingStatus.Completed,
                [4] = AnimeTrackingStatus.Following,
            },
            ExcludedAnimeId: null);

        var picked = TodayThemeService.SelectUnrated(
            [Item(1, 7.0), Item(2, 7.0), Item(3, 7.0), Item(4, 7.0), Item(5, 7.0)],
            context,
            new HashSet<int> { 3 });

        Assert.Equal(new[] { 2, 1 }, picked.Select(anime => anime.ID));
    }

    [Fact]
    public void SelectOneYearAgo_TakesLatestEventInWindowPerAnime()
    {
        var context = new TodayThemeContext(
            Start,
            new Dictionary<int, AnimeTrackingStatus> { [4] = AnimeTrackingStatus.Blocked },
            ExcludedAnimeId: null);
        var center = new DateTimeOffset(2025, 9, 19, 12, 0, 0, TimeSpan.Zero);
        (int, AnimeTrackingStatus, DateTimeOffset)[] events =
        [
            (1, AnimeTrackingStatus.Watching, center.AddDays(-2)),
            (1, AnimeTrackingStatus.Completed, center.AddDays(3)),
            (2, AnimeTrackingStatus.PlanToWatch, center.AddDays(-1)),
            (3, AnimeTrackingStatus.Watching, center.AddDays(-30)),
            (4, AnimeTrackingStatus.Watching, center),
            (5, AnimeTrackingStatus.Following, center),
        ];

        var picked = TodayThemeService.SelectOneYearAgo(events, context);

        Assert.Equal(new[] { 2, 1 }, picked.Select(item => item.AnimeId));
        Assert.Equal(AnimeTrackingStatus.Completed, picked.Single(item => item.AnimeId == 1).NewStatus);
    }

    [Fact]
    public void SelectStalled_UsesLatestOfMarkAndPlaybackAndSkipsPendingPlans()
    {
        var now = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
        var context = new TodayThemeContext(
            Start,
            new Dictionary<int, AnimeTrackingStatus>
            {
                [1] = AnimeTrackingStatus.Watching,
                [2] = AnimeTrackingStatus.PlanToWatch,
                [3] = AnimeTrackingStatus.Watching,
                [4] = AnimeTrackingStatus.PlanToWatch,
                [5] = AnimeTrackingStatus.Completed,
            },
            ExcludedAnimeId: null)
        {
            UpdatedAt = new Dictionary<int, DateTimeOffset>
            {
                [1] = now.AddDays(-90),
                [2] = now.AddDays(-45),
                [3] = now.AddDays(-90),
                [4] = now.AddDays(-200),
                [5] = now.AddDays(-300),
            },
        };
        // 3 虽然标记得早，但最近还在播放。
        var progress = new Dictionary<int, AnimeProgressSnapshot>
        {
            [3] = new(3, 4, 0, 1440, now.AddDays(-2)),
        };

        var picked = TodayThemeService.SelectStalled(context, progress, new HashSet<int> { 4 }, now);

        Assert.Equal(new[] { (1, 90), (2, 45) }, picked);
    }

    [Fact]
    public async Task RevisitedBrowse_ShowsItemsViewedTwiceOrFallsBack()
    {
        await RunProductionMigrationAsync();
        var date = DateWith(TodayThemeKind.RevisitedBrowse);
        var browse = new BrowseHistoryService(DbFactory);
        await browse.RecordAsync(301, "看过的1");

        // 只有一部浏览过（还只看了一次）：不够，用替补。
        var sparse = NewViewModel(new ThemeSource(), new TrackingService(DbFactory));
        await sparse.LoadForDateAsync(date);
        Assert.True(sparse.IsFallback);

        await browse.RecordAsync(301, "看过的1");
        await browse.RecordAsync(302, "看过的2");
        await browse.RecordAsync(302, "看过的2");
        TodayThemeViewModel.ResetSessionCache();
        var vm = NewViewModel(new ThemeSource(), new TrackingService(DbFactory));
        await vm.LoadForDateAsync(date);

        Assert.False(vm.IsFallback);
        Assert.Equal("你看过好几次的", vm.Title);
        Assert.Equal(new[] { 301, 302 }, vm.Items.Select(item => item.Anime.ID).Order());
        Assert.All(vm.Items, item => Assert.StartsWith("看过 2 次", item.Note));
    }

    [Fact]
    public async Task UnratedThisSeason_ListsFinishedAndWatchingWithoutRating()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        await tracking.SetStatusAsync(101, AnimeTrackingStatus.Watching);
        await tracking.SetStatusAsync(102, AnimeTrackingStatus.Completed);
        await tracking.SetStatusAsync(103, AnimeTrackingStatus.Completed);
        await new ArchiveService(DbFactory).UpsertArchiveAsync(103, "作品103", 8.0, string.Empty);
        var date = DateWith(TodayThemeKind.UnratedThisSeason);
        var vm = NewViewModel(new ThemeSource(), tracking);

        await vm.LoadForDateAsync(date);

        Assert.False(vm.IsFallback);
        Assert.Equal(new[] { 102, 101 }, vm.Items.Select(item => item.Anime.ID));
        Assert.All(vm.Items, item => Assert.Equal("评分", item.PrimaryLabel));
        Assert.False(vm.CanShowNextBatch);
    }

    [Fact]
    public async Task Stalled_ListsOldWatchingAndFinishingRemovesActions()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        await tracking.SetStatusWithTimestampAsync(
            401,
            AnimeTrackingStatus.Watching,
            DateTime.UtcNow.AddDays(-60).ToString("O"));
        await tracking.SetStatusAsync(402, AnimeTrackingStatus.Watching);
        var date = DateWith(TodayThemeKind.Stalled);
        var vm = NewViewModel(new ThemeSource(), tracking);

        await vm.LoadForDateAsync(date);

        var item = Assert.Single(vm.Items);
        Assert.Equal(401, item.Anime.ID);
        Assert.StartsWith("停了 6", item.Note);
        Assert.True(item.HasPrimary);

        await vm.ApplyStatusAsync(item, AnimeTrackingStatus.Completed);

        Assert.Equal(AnimeTrackingStatus.Completed, item.Status);
        Assert.False(item.HasActions);
        Assert.Contains(
            await tracking.GetAllTrackingAsync(),
            row => row.AnimeId == 401 && row.Status == AnimeTrackingStatus.Completed);
    }

    [Fact]
    public void PickFavoriteGenre_CountsEachGenreOncePerAnimeAndSkipsSourceTags()
    {
        IReadOnlyList<string>[] tags =
        [
            ["治愈", "漫画改", "日常"],
            ["治愈", "治愈", "奇幻", "漫画改"],
            ["日常", "2024年4月"],
        ];

        // 治愈、日常各 2 次，按题材目录顺序取“日常”；漫画改不算题材。
        Assert.Equal("日常", TodayThemeService.PickFavoriteGenre(tags));
        Assert.Null(TodayThemeService.PickFavoriteGenre([["漫画改", "MADHouse"]]));
    }

    [Fact]
    public void SelectGenreSeeds_UsesHighRatingsOnlyWhenThereAreAtLeastThree()
    {
        var context = new TodayThemeContext(
            Start,
            new Dictionary<int, AnimeTrackingStatus>
            {
                [1] = AnimeTrackingStatus.Completed,
                [2] = AnimeTrackingStatus.Completed,
                [3] = AnimeTrackingStatus.Completed,
                [4] = AnimeTrackingStatus.Completed,
                [5] = AnimeTrackingStatus.Watching,
            },
            ExcludedAnimeId: null);

        // 看完且 8 分以上的有 3 部：只用它们（5 在追番中，不算）。
        var liked = TodayThemeService.SelectGenreSeeds(
            context,
            new Dictionary<int, double> { [1] = 9, [2] = 8, [3] = 10, [4] = 6, [5] = 10 });
        Assert.True(liked.FromHighRatings);
        Assert.Equal(new[] { 1, 2, 3 }, liked.AnimeIds.Order());
        Assert.StartsWith("你打了 8 分以上的 3 部作品里", TodayThemeService.DescribeGenreSample(liked));

        // 只有 1 部：不足 3 部，用全部看完的，说明也随之改变。
        var recent = TodayThemeService.SelectGenreSeeds(context, new Dictionary<int, double> { [1] = 9 });
        Assert.False(recent.FromHighRatings);
        Assert.Equal(new[] { 1, 2, 3, 4 }, recent.AnimeIds.Order());
        Assert.StartsWith("你最近看完的 4 部作品里", TodayThemeService.DescribeGenreSample(recent));
    }

    [Fact]
    public void SelectStudioWorks_KeepsOtherUnmarkedAnimationWorks()
    {
        var context = new TodayThemeContext(
            Start,
            new Dictionary<int, AnimeTrackingStatus> { [3] = AnimeTrackingStatus.Completed },
            ExcludedAnimeId: 4);
        PersonWork[] works =
        [
            new(1, "出发作品", "动画制作"),
            new(2, "作品2", "动画制作"),
            new(3, "作品3", "动画制作"),
            new(4, "作品4", "动画制作"),
            new(5, "作品5", "原作"),
            new(6, "作品6", "动画制作"),
        ];

        var picked = TodayThemeService.SelectStudioWorks(works, seedId: 1, context);

        Assert.Equal(new[] { 2, 6 }, picked.Select(work => work.ID));
    }

    [Fact]
    public async Task SameStudio_StartsFromRatedWorkAndUsesItsAnimationStudio()
    {
        await RunProductionMigrationAsync();
        await new ArchiveService(DbFactory).UpsertArchiveAsync(401, "出发作品", 9.5, string.Empty);
        var date = DateWith(TodayThemeKind.SameStudio);
        var vm = NewViewModel(new ThemeSource(), new TrackingService(DbFactory));

        await vm.LoadForDateAsync(date);

        Assert.False(vm.IsFallback);
        Assert.Equal("你的记录 · Bangumi", vm.SourceText);
        Assert.Contains("《出发作品》出自 动画工作室", vm.Caption);
        Assert.Equal(new[] { 501, 502, 503 }, vm.Items.Select(item => item.Anime.ID).Order());
    }

    [Fact]
    public async Task SameStudio_IsRecomputedAfterRatingChanges()
    {
        // 依赖评分的主题不跨加载缓存：去掉唯一的评分后，同一天再加载就没有出发作品了。
        await RunProductionMigrationAsync();
        var archive = new ArchiveService(DbFactory);
        await archive.UpsertArchiveAsync(401, "出发作品", 9.5, string.Empty);
        var date = DateWith(TodayThemeKind.SameStudio);
        var vm = NewViewModel(new ThemeSource(), new TrackingService(DbFactory));
        await vm.LoadForDateAsync(date);
        Assert.False(vm.IsFallback);

        await archive.UpsertArchiveAsync(401, "出发作品", null, string.Empty);
        await vm.LoadForDateAsync(date);

        Assert.True(vm.IsFallback);
        Assert.Equal("上一季的高分作品", vm.Title);
    }

    [Fact]
    public async Task FavoriteGenre_FollowsRatingChangesAndReusesSearch()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        foreach (var id in new[] { 601, 602, 603, 604, 605 })
            await tracking.SetStatusAsync(id, AnimeTrackingStatus.Completed);
        var source = new ThemeSource();
        var date = DateWith(TodayThemeKind.FavoriteGenre);
        var vm = NewViewModel(source, tracking);

        // 还没有评分：按最近看完的 5 部统计，奇幻 3 部、治愈 2 部。
        await vm.LoadForDateAsync(date);
        Assert.Equal("你偏爱题材的高分作品", vm.Title);
        Assert.StartsWith("你最近看完的 5 部作品里，最常见的题材是“奇幻”", vm.Caption);
        Assert.Equal(1, source.SearchCount);

        // 记录没变：沿用这一天的搜索结果。
        await vm.LoadForDateAsync(date);
        Assert.Equal(1, source.SearchCount);

        // 给 3 部打了高分：改按高分作品统计，治愈 2 部、奇幻 1 部。
        var archive = new ArchiveService(DbFactory);
        foreach (var id in new[] { 601, 602, 603 })
            await archive.UpsertArchiveAsync(id, $"作品{id}", 9, string.Empty);
        await vm.LoadForDateAsync(date);

        Assert.StartsWith("你打了 8 分以上的 3 部作品里，最常见的题材是“治愈”", vm.Caption);
        Assert.Equal("治愈", source.LastTag);
        Assert.Equal(2, source.SearchCount);
    }

    [Fact]
    public async Task EmptyFallbackTheme_IsQueriedOnceAndNotMarkedAsFallback()
    {
        // 轮到的就是替补主题“上一季的高分作品”，它又没有内容时，不再查第二遍，也不标“替补”。
        await RunProductionMigrationAsync();
        var source = new ThemeSource { EmptySeasons = true };
        var date = DateWith(TodayThemeKind.LastSeasonTop);
        var vm = NewViewModel(source, new TrackingService(DbFactory));

        await vm.LoadForDateAsync(date);

        Assert.False(vm.IsFallback);
        Assert.Equal("上一季的高分作品", vm.Title);
        Assert.Empty(vm.Items);
        Assert.True(vm.HasStatusText);
        Assert.Equal(1, source.SeasonRequests);
    }

    [Fact]
    public async Task ApplyStatus_DoesNothingWhileAWriteIsInProgress()
    {
        await RunProductionMigrationAsync();
        var date = DateWith(TodayThemeKind.LastSeasonTop);
        var vm = NewViewModel(new ThemeSource(), new TrackingService(DbFactory));
        await vm.LoadForDateAsync(date);
        var item = vm.Items[0];

        item.IsSavingStatus = true;
        Assert.False(await vm.ApplyStatusAsync(item, AnimeTrackingStatus.PlanToWatch));
        Assert.Equal(AnimeTrackingStatus.None, item.Status);

        item.IsSavingStatus = false;
        Assert.True(await vm.ApplyStatusAsync(item, AnimeTrackingStatus.PlanToWatch));
        Assert.Equal(AnimeTrackingStatus.PlanToWatch, item.Status);
    }

    [Fact]
    public async Task FavoriteGenre_UsesRecentWorksWhenFewerThanThreeAreHighlyRated()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        await tracking.SetStatusAsync(601, AnimeTrackingStatus.Completed);
        await new ArchiveService(DbFactory).UpsertArchiveAsync(601, "喜欢的", 9, string.Empty);
        var source = new ThemeSource();
        var date = DateWith(TodayThemeKind.FavoriteGenre);
        var vm = NewViewModel(source, tracking);

        await vm.LoadForDateAsync(date);

        Assert.False(vm.IsFallback);
        Assert.Equal("治愈", source.LastTag);
        Assert.StartsWith("你最近看完的 1 部作品里，最常见的题材是“治愈”", vm.Caption);
        Assert.NotEmpty(vm.Items);
    }

    private TodayThemeViewModel NewViewModel(IAnimeDataSource source, TrackingService tracking)
        => new(
            source,
            tracking,
            new BrowseHistoryService(DbFactory),
            new ArchiveService(DbFactory),
            new ActionCenterService(DbFactory));

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
        // 601 的标签里“治愈”是题材，“漫画改”是来源。
        private static readonly Dictionary<int, string[]> TagsById = new()
        {
            [601] = ["漫画改", "治愈", "2024年4月"],
            [602] = ["治愈"],
            [603] = ["奇幻"],
            [604] = ["奇幻"],
            [605] = ["奇幻"],
        };

        public bool Fails { get; init; }

        /// <summary>每一季都没有作品。</summary>
        public bool EmptySeasons { get; init; }

        public int SeasonRequests { get; private set; }

        public int SearchCount { get; private set; }

        private static T Unexpected<T>() => throw new NotSupportedException("Unexpected data-source request.");

        // 每一季 20 部带评分的作品。
        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct)
        {
            SeasonRequests++;
            return Fails
                ? Task.FromException<List<Anime>>(new HttpRequestException("offline"))
                : Task.FromResult(EmptySeasons
                    ? new List<Anime>()
                    : Enumerable.Range(101, 20).Select(id => Item(id, 6.0 + (id - 100) * 0.1)).ToList());
        }

        public string? LastTag { get; private set; }

        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort, CancellationToken ct,
            string? airDateFrom = null, string? airDateTo = null)
        {
            LastTag = tag;
            SearchCount++;
            return Fails
                ? Task.FromException<(List<Anime>, int)>(new HttpRequestException("offline"))
                : Task.FromResult((Enumerable.Range(201, 10).Select(id => Item(id, 8.0, AnimeMediaFormat.Movie)).ToList(), 10));
        }

        public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct)
            => Task.FromResult(TagsById.TryGetValue(animeID, out var tags)
                ? tags.Select(name => new Tag(name)).ToList()
                : new List<Tag>());

        // 401 的公司里，出版社排在前面，动画工作室在后面。
        public Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct)
            => Task.FromResult(animeID == 401
                ? new List<Studio> { new(900, "出版社", null), new(901, "动画工作室", null) }
                : new List<Studio>());

        public Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct)
            => Task.FromResult(personId switch
            {
                900 => new List<PersonWork> { new(401, "出发作品", "原作"), new(599, "别的书", "原作") },
                901 => new List<PersonWork>
                {
                    new(401, "出发作品", "动画制作"),
                    new(501, "作品501", "动画制作"),
                    new(502, "作品502", "动画制作"),
                    new(503, "作品503", "动画制作"),
                    new(504, "作品504", "製作"),
                },
                _ => new List<PersonWork>(),
            });

        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct) => Unexpected<Task<List<Anime>>>();
        public Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct)
            => Task.FromResult<Anime?>(Item(animeID, 7.0));
        public Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct) => Unexpected<Task<List<VoiceActor>>>();
        public Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct) => Unexpected<Task<List<CharacterRole>>>();
        public Task<(List<Anime> Results, int Total)> SearchByKeywordAsync(string keyword, int offset, CancellationToken ct)
            => Unexpected<Task<(List<Anime>, int)>>();
    }
}
