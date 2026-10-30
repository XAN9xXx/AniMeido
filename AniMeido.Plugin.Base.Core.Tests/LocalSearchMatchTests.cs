using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

/// <summary>本地搜索的匹配范围：标题与别名，不含制作公司和简介。</summary>
public sealed class LocalSearchMatchTests
{
    private static readonly Anime Frieren = new(
        1,
        "葬送的芙莉莲",
        "MADHOUSE",
        [],
        null,
        null,
        "勇者一行人打倒魔王后，精灵少女芙莉莲踏上了新的旅程。",
        2023,
        10,
        AlternateTitles: ["葬送のフリーレン", "Frieren: Beyond Journey's End"]);

    [Theory]
    [InlineData("芙莉莲")]
    [InlineData("frieren")]
    [InlineData("フリーレン")]
    [InlineData("FRIEREN")]
    public void MatchesQuery_FindsTitleAndAliases(string query)
        => Assert.True(LocalSearchService.MatchesQuery(Frieren, query));

    [Theory]
    [InlineData("MADHOUSE")]
    [InlineData("madhouse")]
    public void MatchesQuery_DoesNotMatchStudio(string query)
        => Assert.False(LocalSearchService.MatchesQuery(Frieren, query));

    [Theory]
    [InlineData("frieren")]
    [InlineData("FRIEREN")]
    [InlineData("FrIeReN")]
    public void MatchesQuery_TitleMatchIsCaseInsensitive(string query)
    {
        var anime = Frieren with { Title = "Frieren", AlternateTitles = null };
        Assert.True(LocalSearchService.MatchesQuery(anime, query));
    }

    [Fact]
    public void MatchesQuery_IgnoresDescription()
        => Assert.False(LocalSearchService.MatchesQuery(Frieren, "少女"));
}
