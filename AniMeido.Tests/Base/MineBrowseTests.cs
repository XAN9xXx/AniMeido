using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

/// <summary>“我的番剧”的状态分组、行内文字、组装与排序。</summary>
public sealed class MineBrowseTests
{
    private static readonly DateOnly Today = new(2026, 9, 24);

    [Fact]
    public void Statuses_CoverEveryManagedStatusOnce()
    {
        var all = MineBrowse.MainStatuses.Concat(MineBrowse.HiddenStatuses).ToList();

        Assert.Equal(
            Enum.GetValues<AnimeTrackingStatus>().Where(status => status != AnimeTrackingStatus.None).Order(),
            all.Order());
        Assert.Equal(AnimeTrackingStatus.Watching, MineBrowse.MainStatuses[0]);
        Assert.True(MineBrowse.IsHidden(AnimeTrackingStatus.Blocked));
        Assert.False(MineBrowse.IsHidden(AnimeTrackingStatus.Dropped));
    }

    [Fact]
    public void StatusSections_KeepMainStatusesFirst()
    {
        var order = TrackingStatusSection.CreateDefaults().Select(section => section.Status);

        Assert.Equal(MineBrowse.MainStatuses.Concat(MineBrowse.HiddenStatuses), order);
    }

    [Theory]
    [InlineData(0, "标记于今天")]
    [InlineData(1, "标记于昨天")]
    [InlineData(3, "标记于 3 天前")]
    [InlineData(14, "标记于 2 周前")]
    [InlineData(95, "标记于 3 个月前")]
    [InlineData(800, "标记于 2 年前")]
    [InlineData(-2, "标记于今天")]
    public void MarkedText_UsesRelativeWords(int daysAgo, string expected)
        => Assert.Equal(expected, MineBrowse.MarkedText(Today.AddDays(-daysAgo), Today));

    [Fact]
    public void MarkedText_IsEmptyWithoutTime()
        => Assert.Equal("", MineBrowse.MarkedText(null, Today));

    [Theory]
    [InlineData("2026-09-20T12:30:00.0000000Z", true)]
    [InlineData("2026-09-20 12:30:00", true)]
    [InlineData("", false)]
    [InlineData("not a date", false)]
    public void ParseMarkedAt_AcceptsCommonFormats(string raw, bool parsed)
        => Assert.Equal(parsed, MineBrowse.ParseMarkedAt(raw) is not null);

    [Fact]
    public void MetaText_ListsDateFormatAndScore()
    {
        Assert.Equal(
            "2026/7/11 · TV动画 · Bangumi 7.8",
            MineBrowse.MetaText(Anime(1, "甲", 7.8, new DateOnly(2026, 7, 11), AnimeMediaFormat.Television)));
        Assert.Equal(
            "开播日期未定 · 暂无评分",
            MineBrowse.MetaText(Anime(2, "乙", null, null, AnimeMediaFormat.Unknown)));
    }

    [Fact]
    public void BuildEntries_SkipsMissingDetailsAndAttachesRatings()
    {
        var at = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        var anime = new Dictionary<int, Anime> { [1] = Anime(1, "甲"), [3] = Anime(3, "丙") };
        var ratings = new Dictionary<int, double> { [3] = 9.5 };

        var entries = MineBrowse.BuildEntries(
            [
                (1, AnimeTrackingStatus.Completed, at),
                (2, AnimeTrackingStatus.Completed, at),
                (3, AnimeTrackingStatus.Completed, null),
            ],
            anime,
            ratings,
            Today);

        Assert.Equal(new[] { 1, 3 }, entries.Select(entry => entry.Anime.ID));
        Assert.False(entries[0].HasMyRating);
        Assert.Equal("我的评分 9.5", entries[1].MyRatingText);
        Assert.Equal("", entries[1].MarkedText);
        Assert.Equal("已看完", entries[0].StatusLabel);
    }

    [Fact]
    public void Sort_RecentlyMarkedPutsMissingTimesLast()
    {
        var entries = new[]
        {
            Entry(1, marked: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)),
            Entry(2, marked: null),
            Entry(3, marked: new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero)),
        };

        var sorted = MineBrowse.Sort(entries, MineSortKey.RecentlyMarked);

        Assert.Equal(new[] { 3, 1, 2 }, sorted.Select(entry => entry.Anime.ID));
    }

    [Fact]
    public void Sort_ByScoreKeepsUnratedLast()
    {
        var entries = new[] { Entry(1, score: 7.1), Entry(2, score: null), Entry(3, score: 8.4), Entry(4, score: 0) };

        var sorted = MineBrowse.Sort(entries, MineSortKey.Score);

        Assert.Equal(new[] { 3, 1, 2, 4 }, sorted.Select(entry => entry.Anime.ID));
    }

    [Fact]
    public void Sort_ByTitleMixesChinesePinyinAndEnglish()
    {
        var titles = TitleSortComparerTests.MixedTitles;
        var entries = titles.Select((title, index) => Entry(index + 1, title: title)).Reverse();

        var sorted = MineBrowse.Sort(entries, MineSortKey.Title);

        Assert.Equal(titles, sorted.Select(entry => entry.Anime.Title));
    }

    [Fact]
    public void Sort_ByTitleBreaksIdenticalTitlesByAscendingId()
    {
        var entries = new[] { Entry(3, title: "银魂"), Entry(1, title: "银魂"), Entry(2, title: "银魂") };

        Assert.Equal(new[] { 1, 2, 3 }, MineBrowse.Sort(entries, MineSortKey.Title)
            .Select(entry => entry.Anime.ID));
        Assert.Equal(new[] { 1, 2, 3 }, MineBrowse.Sort(entries.Reverse(), MineSortKey.Title)
            .Select(entry => entry.Anime.ID));
    }

    [Fact]
    public void CountByStatus_IncludesEmptyStatuses()
    {
        var counts = MineBrowse.CountByStatus(
            [AnimeTrackingStatus.Watching, AnimeTrackingStatus.Watching, AnimeTrackingStatus.Blocked]);

        Assert.Equal(2, counts[AnimeTrackingStatus.Watching]);
        Assert.Equal(1, counts[AnimeTrackingStatus.Blocked]);
        Assert.Equal(0, counts[AnimeTrackingStatus.Following]);
        Assert.Equal(7, counts.Count);
    }

    private static Anime Anime(
        int id,
        string title,
        double? score = null,
        DateOnly? airDate = null,
        AnimeMediaFormat format = AnimeMediaFormat.Television)
        => new(id, title, null, [], airDate, null, "", 2026, 7, Score: score, MediaFormat: format);

    private static MineEntry Entry(
        int id,
        string? title = null,
        double? score = null,
        DateTimeOffset? marked = null)
        => new(Anime(id, title ?? $"作品{id}", score), AnimeTrackingStatus.Watching, marked, null, "");
}
