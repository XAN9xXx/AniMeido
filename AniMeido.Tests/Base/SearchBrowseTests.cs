using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

/// <summary>搜索页的最近搜索、分页合并、排序与计数文字。</summary>
public sealed class SearchBrowseTests
{
    [Fact]
    public void PushRecent_MovesRepeatedQueryToFrontIgnoringCase()
    {
        var recent = SearchBrowse.PushRecent(["芙莉莲", "Bocchi", "孤独摇滚"], " bocchi ");

        Assert.Equal(new[] { "bocchi", "芙莉莲", "孤独摇滚" }, recent);
    }

    [Fact]
    public void PushRecent_KeepsAtMostLimit()
    {
        var recent = Enumerable.Range(1, SearchBrowse.RecentLimit).Select(i => $"词{i}").ToList();

        var pushed = SearchBrowse.PushRecent(recent, "新词");

        Assert.Equal(SearchBrowse.RecentLimit, pushed.Count);
        Assert.Equal("新词", pushed[0]);
        Assert.DoesNotContain($"词{SearchBrowse.RecentLimit}", pushed);
    }

    [Fact]
    public void PushRecent_IgnoresBlankQuery()
    {
        IReadOnlyList<string> recent = ["甲"];

        Assert.Same(recent, SearchBrowse.PushRecent(recent, "   "));
    }

    [Fact]
    public void AppendPage_SkipsDuplicatesAcrossPages()
    {
        var merged = SearchBrowse.AppendPage([Anime(1), Anime(2)], [Anime(2), Anime(3), Anime(3)]);

        Assert.Equal(new[] { 1, 2, 3 }, merged.Select(anime => anime.ID));
    }

    [Fact]
    public void Sort_MatchKeepsSourceOrder()
    {
        var entries = Entries(Anime(1, 6.0), Anime(2, 9.0), Anime(3, 7.0));

        Assert.Equal(new[] { 1, 2, 3 }, SearchBrowse.Sort(entries, SearchSortKey.Match).Select(entry => entry.Anime.ID));
        Assert.Equal(new[] { 2, 3, 1 }, SearchBrowse.Sort(entries, SearchSortKey.Score).Select(entry => entry.Anime.ID));
    }

    [Fact]
    public void Sort_AirDateNewestFirstWithMissingLast()
    {
        var entries = Entries(
            Anime(1, airDate: new DateOnly(2020, 1, 1)),
            Anime(2),
            Anime(3, airDate: new DateOnly(2024, 1, 1)));

        Assert.Equal(new[] { 3, 1, 2 }, SearchBrowse.Sort(entries, SearchSortKey.AirDate).Select(entry => entry.Anime.ID));
    }

    [Theory]
    [InlineData(128, 40, "共 128 部 · 已加载 40 部")]
    [InlineData(12, 12, "共 12 部")]
    public void Summary_ShowsLoadedCountUntilAllLoaded(int total, int loaded, string expected)
        => Assert.Equal(expected, SearchBrowse.Summary(total, loaded));

    private static Anime Anime(int id, double? score = null, DateOnly? airDate = null)
        => new(id, $"作品{id}", null, [], airDate, null, "", 2024, 1, Score: score);

    private static IReadOnlyList<PastSeasonEntry> Entries(params Anime[] anime)
        => anime.Select(item => new PastSeasonEntry(item, AnimeTrackingStatus.None)).ToList();
}
