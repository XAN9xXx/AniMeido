using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

/// <summary>番剧库的筛选、形态计数、排序与季度切换。</summary>
public sealed class PastSeasonBrowseTests
{
    private static readonly PastSeasonTarget Earliest = new(1900, Season.Winter);
    private static readonly PastSeasonTarget Latest = new(2026, Season.Spring);

    [Fact]
    public void Visible_RemovesBlockedAndMatchesTitle()
    {
        var source = new[] { Anime(1, "星轨邮差"), Anime(2, "星期三的烘焙社"), Anime(3, "猫町散步") };
        var statuses = Statuses((2, AnimeTrackingStatus.Blocked));

        var visible = PastSeasonBrowse.Visible(source, statuses, " 星 ", hideCompleted: false);

        Assert.Equal(new[] { 1 }, visible.Select(entry => entry.Anime.ID));
    }

    [Fact]
    public void Visible_HidesOnlyCompletedWhenAsked()
    {
        var source = new[] { Anime(1, "甲"), Anime(2, "乙"), Anime(3, "丙") };
        var statuses = Statuses(
            (1, AnimeTrackingStatus.Completed),
            (2, AnimeTrackingStatus.Dropped));

        var shown = PastSeasonBrowse.Visible(source, statuses, null, hideCompleted: false);
        var hidden = PastSeasonBrowse.Visible(source, statuses, null, hideCompleted: true);

        Assert.Equal(new[] { 1, 2, 3 }, shown.Select(entry => entry.Anime.ID));
        Assert.Equal(new[] { 2, 3 }, hidden.Select(entry => entry.Anime.ID));
        Assert.Equal(AnimeTrackingStatus.Dropped, hidden[0].Status);
        Assert.Equal(AnimeTrackingStatus.None, hidden[1].Status);
    }

    [Fact]
    public void FormatChips_CountBeforeFormatFilterAndSkipEmptyFormats()
    {
        var visible = Entries(
            Anime(1, "a", format: AnimeMediaFormat.Television),
            Anime(2, "b", format: AnimeMediaFormat.Television),
            Anime(3, "c", format: AnimeMediaFormat.Movie));

        var chips = PastSeasonBrowse.FormatChips(visible, AnimeMediaFormat.Movie);

        Assert.Equal(
            new (AnimeMediaFormat?, int, bool)[]
            {
                (null, 3, false),
                (AnimeMediaFormat.Television, 2, false),
                (AnimeMediaFormat.Movie, 1, true),
            },
            chips.Select(chip => (chip.Format, chip.Count, chip.IsSelected)));
    }

    [Fact]
    public void FormatChips_KeepSelectedFormatEvenWhenEmpty()
    {
        var visible = Entries(Anime(1, "a", format: AnimeMediaFormat.Television));

        var chips = PastSeasonBrowse.FormatChips(visible, AnimeMediaFormat.Ova);

        Assert.Contains(chips, chip => chip is { Format: AnimeMediaFormat.Ova, Count: 0, IsSelected: true });
    }

    [Fact]
    public void ByFormat_GroupsUnlistedFormatsUnderOther()
    {
        var visible = Entries(
            Anime(1, "a", format: AnimeMediaFormat.Unknown),
            Anime(2, "b", format: (AnimeMediaFormat)4),
            Anime(3, "c", format: AnimeMediaFormat.Television));

        var other = PastSeasonBrowse.ByFormat(visible, AnimeMediaFormat.Unknown);

        Assert.Equal(new[] { 1, 2 }, other.Select(entry => entry.Anime.ID));
    }

    [Theory]
    [InlineData(false, new[] { 2, 1, 3, 4 })]
    [InlineData(true, new[] { 3, 1, 2, 4 })]
    public void Sort_ByScoreKeepsMissingScoresLast(bool ascending, int[] expected)
    {
        var entries = Entries(
            Anime(1, "a", score: 7.5),
            Anime(2, "b", score: 8.8),
            Anime(3, "c", score: 6.1),
            Anime(4, "d", score: null));

        var sorted = PastSeasonBrowse.Sort(entries, PastSeasonSortKey.Score, ascending);

        Assert.Equal(expected, sorted.Select(entry => entry.Anime.ID));
    }

    [Theory]
    [InlineData(true, new[] { 2, 1, 3 })]
    [InlineData(false, new[] { 1, 2, 3 })]
    public void Sort_ByAirDateKeepsMissingDatesLast(bool ascending, int[] expected)
    {
        var entries = Entries(
            Anime(1, "a", airDate: new DateOnly(2026, 5, 1)),
            Anime(2, "b", airDate: new DateOnly(2026, 4, 1)),
            Anime(3, "c", airDate: null));

        var sorted = PastSeasonBrowse.Sort(entries, PastSeasonSortKey.AirDate, ascending);

        Assert.Equal(expected, sorted.Select(entry => entry.Anime.ID));
    }

    [Fact]
    public void Sort_ByTitleUsesChineseOrder()
    {
        var entries = Entries(Anime(1, "猫町散步"), Anime(2, "阿尔卑斯"), Anime(3, "Re：从零开始"));

        var sorted = PastSeasonBrowse.Sort(entries, PastSeasonSortKey.Title, ascending: true);

        Assert.Equal(3, sorted[0].Anime.ID);
        Assert.True(
            sorted.ToList().FindIndex(entry => entry.Anime.ID == 2)
                < sorted.ToList().FindIndex(entry => entry.Anime.ID == 1));
    }

    [Theory]
    [InlineData(PastSeasonSortKey.Score, false)]
    [InlineData(PastSeasonSortKey.AirDate, true)]
    [InlineData(PastSeasonSortKey.Title, true)]
    public void DefaultAscending_MatchesEachKey(PastSeasonSortKey key, bool expected)
        => Assert.Equal(expected, PastSeasonBrowse.DefaultAscending(key));

    [Theory]
    [InlineData(2026, Season.Winter, -1, 2025, Season.Fall)]
    [InlineData(2025, Season.Fall, 1, 2026, Season.Winter)]
    [InlineData(2026, Season.Winter, 1, 2026, Season.Spring)]
    public void Step_CrossesYearBoundaries(int year, Season season, int delta, int expectedYear, Season expectedSeason)
        => Assert.Equal(
            new PastSeasonTarget(expectedYear, expectedSeason),
            PastSeasonBrowse.Step(new PastSeasonTarget(year, season), delta, Earliest, Latest));

    [Fact]
    public void Step_StopsAtBothEnds()
    {
        Assert.Null(PastSeasonBrowse.Step(Latest, 1, Earliest, Latest));
        Assert.Null(PastSeasonBrowse.Step(Earliest, -1, Earliest, Latest));
    }

    [Fact]
    public void Clamp_PullsTargetsIntoRange()
    {
        Assert.Equal(Latest, PastSeasonBrowse.Clamp(new PastSeasonTarget(2026, Season.Fall), Earliest, Latest));
        Assert.Equal(Earliest, PastSeasonBrowse.Clamp(new PastSeasonTarget(1800, Season.Spring), Earliest, Latest));
    }

    [Theory]
    [InlineData(247, 247, "2026 年春季 · 247 部")]
    [InlineData(12, 247, "2026 年春季 · 显示 12 / 247 部")]
    public void Summary_ShowsFilteredCountOnlyWhenFiltered(int shown, int total, string expected)
        => Assert.Equal(expected, PastSeasonBrowse.Summary(Latest, shown, total));

    [Fact]
    public void SyncInPlace_KeepsUnchangedItemsAndReplacesChangedStatus()
    {
        var a = Anime(1, "甲");
        var b = Anime(2, "乙");
        var c = Anime(3, "丙");
        var current = new System.Collections.ObjectModel.ObservableCollection<PastSeasonEntry>(Entries(a, b, c));
        var untouched = current[0];
        var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        current.CollectionChanged += (_, e) => actions.Add(e.Action);

        var synced = PastSeasonBrowse.SyncInPlace(current, new[]
        {
            new PastSeasonEntry(a, AnimeTrackingStatus.None),
            new PastSeasonEntry(b, AnimeTrackingStatus.Watching),
            new PastSeasonEntry(c, AnimeTrackingStatus.None),
        });

        Assert.True(synced);
        Assert.Same(untouched, current[0]);
        Assert.Equal(AnimeTrackingStatus.Watching, current[1].Status);
        Assert.Equal([System.Collections.Specialized.NotifyCollectionChangedAction.Replace], actions);
    }

    [Fact]
    public void SyncInPlace_RemovesAndInsertsToMatchTarget()
    {
        var a = Anime(1, "甲");
        var b = Anime(2, "乙");
        var c = Anime(3, "丙");
        var d = Anime(4, "丁");
        var current = new System.Collections.ObjectModel.ObservableCollection<PastSeasonEntry>(Entries(a, b, c));

        var synced = PastSeasonBrowse.SyncInPlace(current, Entries(a, d, c));

        Assert.True(synced);
        Assert.Equal(new[] { 1, 4, 3 }, current.Select(entry => entry.Anime.ID));
    }

    [Fact]
    public void SyncInPlace_RefusesDuplicateIds()
    {
        var a = Anime(1, "甲");
        var current = new System.Collections.ObjectModel.ObservableCollection<PastSeasonEntry>(Entries(a));

        Assert.False(PastSeasonBrowse.SyncInPlace(current, Entries(a, a)));
    }

    private static Anime Anime(
        int id,
        string title,
        double? score = null,
        DateOnly? airDate = null,
        AnimeMediaFormat format = AnimeMediaFormat.Television)
        => new(id, title, null, [], airDate, null, "", 2026, 4, Score: score, MediaFormat: format);

    private static IReadOnlyList<PastSeasonEntry> Entries(params Anime[] anime)
        => anime.Select(item => new PastSeasonEntry(item, AnimeTrackingStatus.None)).ToList();

    private static IReadOnlyDictionary<int, AnimeTrackingStatus> Statuses(
        params (int Id, AnimeTrackingStatus Status)[] rows)
        => rows.ToDictionary(row => row.Id, row => row.Status);
}
