using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

/// <summary>本地搜索的匹配范围：标题、别名与制作公司，不含简介。</summary>
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
    [InlineData("madhouse")]
    public void MatchesQuery_FindsTitleAliasAndStudio(string query)
        => Assert.True(LocalSearchService.MatchesQuery(Frieren, query));

    [Fact]
    public void MatchesQuery_IgnoresDescription()
        => Assert.False(LocalSearchService.MatchesQuery(Frieren, "少女"));
}
