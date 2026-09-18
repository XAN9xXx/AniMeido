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
    public void RankDiscover_OnlyIncludesUnmarkedScoredEntriesByScore()
    {
        // 不感兴趣等任何标记都算“已标记”，不再推荐。
        var entries = CurrentSeasonViewModel.BuildEntries(
            [Item(1, 1, 7.5), Item(2, 2, 8.8), Item(3, 3, null), Item(4, 4, 9.1)],
            new Dictionary<int, AnimeTrackingStatus>
            {
                [4] = AnimeTrackingStatus.NotInterested,
            });

        var ranked = CurrentSeasonViewModel.RankDiscover(entries);

        Assert.Equal(new[] { 2, 1 }, ranked.Select(entry => entry.Anime.ID));
    }

    [Fact]
    public void ExtractOthers_KeepsOnlySeasonAnimeMissingFromWeeklySchedule()
    {
        var others = CurrentSeasonViewModel.ExtractOthers(
            [Item(1, 1, 7.0), Movie(10, new DateOnly(2026, 8, 7)), Movie(10, new DateOnly(2026, 8, 7))],
            [Item(1, 1, 7.0)]);

        Assert.Equal(new[] { 10 }, others.Select(anime => anime.ID));
    }

    [Fact]
    public void Others_AreOrderedByDateAndLeftOutOfDiscover()
    {
        // “其他”固定按上映日期排列，没有日期的放最后；本季发现只推荐周更作品。
        var entries = CurrentSeasonViewModel.BuildEntries(
            [Item(1, 1, 7.0)],
            [Movie(10, null, 9.5), Movie(11, new DateOnly(2026, 9, 1), 9.0), Movie(12, new DateOnly(2026, 7, 20), 8.0)],
            new Dictionary<int, AnimeTrackingStatus>());

        var ordered = CurrentSeasonViewModel.OrderOthers(entries.Where(entry => entry.IsOther));
        var ranked = CurrentSeasonViewModel.RankDiscover(entries);

        Assert.Equal(new[] { 12, 11, 10 }, ordered.Select(entry => entry.Anime.ID));
        Assert.Equal(new[] { 1 }, ranked.Select(entry => entry.Anime.ID));
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
        score);

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
