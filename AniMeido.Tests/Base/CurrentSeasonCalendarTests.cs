using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

public sealed class CurrentSeasonCalendarTests
{
    [Fact]
    public void BuildEntries_ExcludesBlockedAndCarriesLocalStatus()
    {
        var entries = CurrentSeasonViewModel.BuildEntries(
            [Item(1, 4, 7.0), Item(2, 4, 8.0), Item(3, 5, 6.0)],
            new Dictionary<int, AnimeTrackingStatus>
            {
                [1] = AnimeTrackingStatus.Watching,
                [2] = AnimeTrackingStatus.Blocked,
            });

        Assert.Equal(new[] { 1, 3 }, entries.Select(entry => entry.Anime.ID));
        Assert.Equal(AnimeTrackingStatus.Watching, entries[0].Status);
        Assert.Equal(AnimeTrackingStatus.None, entries[1].Status);
    }

    [Fact]
    public void Order_PutsMarkedFirstThenScoreWithUnscoredLast()
    {
        var entries = CurrentSeasonViewModel.BuildEntries(
            [Item(1, 4, 8.5), Item(2, 4, null), Item(3, 4, 6.0), Item(4, 4, 7.0)],
            new Dictionary<int, AnimeTrackingStatus>
            {
                [3] = AnimeTrackingStatus.Following,
            });

        var ordered = CurrentSeasonViewModel.Order(entries, CalendarSort.Score);

        Assert.Equal(new[] { 3, 1, 4, 2 }, ordered.Select(entry => entry.Anime.ID));
    }

    [Fact]
    public void RankTimeMachine_KeepsMarkedWithStatusAndDropsBlockedOrUnscored()
    {
        // 时光机保留已标记的作品并显示状态；屏蔽的与没有评分的不显示。
        var ranked = CurrentSeasonViewModel.RankTimeMachine(
            [Item(1, 1, 7.5), Item(2, 2, 8.8), Item(3, 3, null), Item(4, 4, 9.1), Item(5, 5, 8.0)],
            new Dictionary<int, AnimeTrackingStatus>
            {
                [2] = AnimeTrackingStatus.Completed,
                [4] = AnimeTrackingStatus.Blocked,
            });

        Assert.Equal(new[] { 2, 5, 1 }, ranked.Select(entry => entry.Anime.ID));
        Assert.Equal(new[] { 1, 2, 3 }, ranked.Select(entry => entry.Rank));
        Assert.Equal(AnimeTrackingStatus.Completed, ranked[0].Status);
        Assert.True(ranked[0].HasStatus);
        Assert.False(ranked[1].HasStatus);
    }

    [Fact]
    public void ExtractOthers_KeepsOnlySeasonAnimeMissingFromWeeklySchedule()
    {
        var others = CurrentSeasonViewModel.ExtractOthers(
            [Item(1, 1, 7.0), Movie(10, new DateOnly(2026, 8, 7)), Movie(10, new DateOnly(2026, 8, 7))],
            [Item(1, 1, 7.0)]);

        Assert.Equal(new[] { 10 }, others.Select(anime => anime.ID));
    }

    [Theory]
    [InlineData(AnimeMediaFormat.Television, false)]
    [InlineData(AnimeMediaFormat.Ona, true)]
    [InlineData(AnimeMediaFormat.Ova, true)]
    [InlineData(AnimeMediaFormat.Movie, true)]
    [InlineData(AnimeMediaFormat.Unknown, true)]
    public void BuildEntries_OnlyConfirmedTvUsesWeekday(AnimeMediaFormat format, bool isOther)
    {
        var entry = Assert.Single(CurrentSeasonViewModel.BuildEntries(
            [Item(1, 4, 7.0) with { MediaFormat = format }],
            new Dictionary<int, AnimeTrackingStatus>()));

        Assert.Equal(isOther, entry.IsOther);
        Assert.Equal(isOther ? CalendarDay.OtherKey : 4, entry.DayKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(8)]
    public void BuildEntries_TvWithoutValidWeekdayGoesToOther(int? weekday)
    {
        var entry = Assert.Single(CurrentSeasonViewModel.BuildEntries(
            [Item(1, 4, 7.0) with { Weekday = weekday }],
            new Dictionary<int, AnimeTrackingStatus>()));

        Assert.True(entry.IsOther);
        Assert.Equal(CalendarDay.OtherKey, entry.DayKey);
    }

    [Fact]
    public void ResolveScheduleFormats_PreservesCalendarDataAndDoesNotMutateSource()
    {
        var calendar = Item(1, 4, 7.0) with { MediaFormat = AnimeMediaFormat.Unknown };
        var catalog = Item(1, 1, 9.0) with { AirDate = new DateOnly(2026, 7, 20) };
        var resolved = Assert.Single(CurrentSeasonViewModel.ResolveScheduleFormats([calendar], [catalog]));

        Assert.Equal(calendar with { MediaFormat = AnimeMediaFormat.Television }, resolved);
        Assert.Equal(AnimeMediaFormat.Unknown, calendar.MediaFormat);
        Assert.Equal(4, resolved.Weekday);
        Assert.Equal(7.0, resolved.Score);
    }

    [Fact]
    public void ResolveScheduleFormats_MissingAndUnknownCatalogFormatsAreNotGuessed()
    {
        var schedule = new[]
        {
            Item(1, 4, 7.0) with { MediaFormat = AnimeMediaFormat.Unknown },
            Item(2, 4, 7.0) with { MediaFormat = AnimeMediaFormat.Unknown },
            Item(3, 4, 7.0),
        };
        var resolved = CurrentSeasonViewModel.ResolveScheduleFormats(
            schedule,
            [schedule[0], schedule[2] with { MediaFormat = AnimeMediaFormat.Unknown }]);
        var entries = CurrentSeasonViewModel.BuildEntries(resolved, new Dictionary<int, AnimeTrackingStatus>());

        Assert.Equal(new[] { 1, 2 }, entries.Where(entry => entry.IsOther).Select(entry => entry.Anime.ID));
        Assert.Equal(3, Assert.Single(entries.Where(entry => !entry.IsOther)).Anime.ID);
    }

    [Fact]
    public void BuildEntries_DeduplicatesCalendarAndCatalogAndKeepsUnscheduledTvInOther()
    {
        var entries = CurrentSeasonViewModel.BuildEntries(
            [Item(1, 4, 7.0)],
            [Item(1, 4, 7.0), Item(2, 4, 7.0)],
            new Dictionary<int, AnimeTrackingStatus>());

        Assert.Equal(2, entries.Count);
        Assert.False(entries[0].IsOther);
        Assert.True(entries[1].IsOther);
    }

    [Fact]
    public void UpdateAnime_NotifiesFormatAndGroupingWithoutReplacingStatusState()
    {
        var anime = Item(1, 4, 7.0) with { MediaFormat = AnimeMediaFormat.Unknown };
        var entry = new CalendarEntry(anime, isOther: true)
        {
            Status = AnimeTrackingStatus.Following,
            IsSavingStatus = true,
        };
        var changed = new List<string?>();
        entry.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        Assert.True(entry.UpdateAnime(anime with { MediaFormat = AnimeMediaFormat.Television }, isOther: false));

        Assert.Contains(nameof(CalendarEntry.Anime), changed);
        Assert.Contains(nameof(CalendarEntry.DayKey), changed);
        Assert.Contains(nameof(CalendarEntry.ShelfBadgeText), changed);
        Assert.Contains(nameof(CalendarEntry.ShowWatchAction), changed);
        Assert.Equal(4, entry.DayKey);
        Assert.Null(entry.ShelfBadgeText);
        Assert.True(entry.IsSavingStatus);
        Assert.Equal(AnimeTrackingStatus.Following, entry.Status);
    }

    [Fact]
    public void Others_AreOrderedByDate()
    {
        // “其他”固定按上映日期排列，没有日期的放最后。
        var entries = CurrentSeasonViewModel.BuildEntries(
            [Item(1, 1, 7.0)],
            [Movie(10, null, 9.5), Movie(11, new DateOnly(2026, 9, 1), 9.0), Movie(12, new DateOnly(2026, 7, 20), 8.0)],
            new Dictionary<int, AnimeTrackingStatus>());

        var ordered = CurrentSeasonViewModel.OrderOthers(entries.Where(entry => entry.IsOther));

        Assert.Equal(new[] { 12, 11, 10 }, ordered.Select(entry => entry.Anime.ID));
    }

    [Fact]
    public void Others_PutWatchingAndFollowingFirstThenDateAndTitle()
    {
        var entries = CurrentSeasonViewModel.BuildEntries(
            [],
            [Movie(10, null), Movie(11, new DateOnly(2026, 9, 1)),
                Movie(12, new DateOnly(2026, 7, 20)), Movie(13, new DateOnly(2026, 8, 1)),
                Movie(14, new DateOnly(2026, 11, 1)), Movie(15, new DateOnly(2026, 8, 1))],
            new Dictionary<int, AnimeTrackingStatus>
            {
                [10] = AnimeTrackingStatus.Watching,
                [13] = AnimeTrackingStatus.Following,
                [14] = AnimeTrackingStatus.Watching,
                [15] = AnimeTrackingStatus.Following,
            });

        var ordered = CurrentSeasonViewModel.OrderOthers(entries);

        Assert.Equal(new[] { 13, 15, 14, 10, 12, 11 }, ordered.Select(entry => entry.Anime.ID));
    }

    [Fact]
    public void OtherEntry_OffersWatchOnlyAfterRelease()
    {
        var released = new CalendarEntry(Movie(10, DateOnly.FromDateTime(DateTime.Today)), isOther: true);
        var upcoming = new CalendarEntry(Movie(11, DateOnly.FromDateTime(DateTime.Today).AddDays(1)), isOther: true);
        var weekly = new CalendarEntry(Item(1, 1, 7.0));

        Assert.True(released.ShowWatchAction);
        Assert.False(upcoming.ShowWatchAction);
        Assert.True(weekly.ShowWatchAction);
        Assert.Equal("剧场版", released.WeekdayText);
        Assert.Equal(CalendarDay.OtherKey, released.DayKey);
    }

    [Theory]
    [InlineData(false, "9 部")]
    [InlineData(true, "2 / 9 部")]
    public void DayCountText_ShowsMatchesOnlyWhileFiltering(bool filtering, string expected)
        => Assert.Equal(
            expected,
            CurrentSeasonViewModel.BuildDayCountText(2, 9, filtering));

    private static Anime Item(int id, int weekday, double? score) => new(
        id,
        $"作品{id}",
        null,
        [],
        new DateOnly(2026, 7, weekday),
        null,
        string.Empty,
        2026,
        7,
        weekday,
        score,
        MediaFormat: AnimeMediaFormat.Television);

    private static Anime Movie(int id, DateOnly? airDate, double? score = null) => new(
        id,
        $"剧场版{id}",
        null,
        [],
        airDate,
        null,
        string.Empty,
        2026,
        7,
        Score: score,
        MediaFormat: AnimeMediaFormat.Movie);
}
