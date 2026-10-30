using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Player.Sources;

namespace AniMeido.Tests;

public sealed class TitleMatcherTests
{
    public static TheoryData<string, string, bool> SeasonAndPartCompatibility { get; } = new()
    {
        { "Example Season 1 Part 1", "Example Season 2 Part 1", false },
        { "Example Season 2 Part 1", "Example Season 1 Part 1", false },
        { "Example 第1季 第1部", "Example 第2季 第1部", false },
        { "Example 第2季 第1部", "Example 第1季 第1部", false },
        { "Example Season 1 Part 1", "Example Season 1 Part 2", false },
        { "Example Season 1 Part 2", "Example Season 1 Part 1", false },
        { "Example 第1季 第1部", "Example 第1季 第2部", false },
        { "Example 第1季 第2部", "Example 第1季 第1部", false },
        { "Example Season 1 Part 1", "Example 第2季 第1部", false },
        { "Example Season 1 Part 1", "Example Season 1 Part 1", true },
        { "Example Season 1 Part 1", "Example 第1季 第1部", true },
        { "Example Season 1 Part 1", "Example Season 1", true },
        { "Example Season 1", "Example Season 1 Part 1", true },
        { "Example Season 1 Part 1", "Example Part 1", true },
        { "Example Part 1", "Example Season 1 Part 1", true },
        { "Example Season 1", "Example Part 1", false },
        { "Example Part 1", "Example Season 1", false },
        { "Example", "Example Season 2 Part 3", true },
        { "Example Season 2 Part 3", "Example", true },
    };

    [Theory]
    [MemberData(nameof(SeasonAndPartCompatibility))]
    public void Rank_RequiresAllSharedSeasonAndPartKindsToAgree(
        string title,
        string candidateTitle,
        bool compatible)
    {
        var context = new AnimePlaybackContext(42, title);
        var candidate = new SourceAnimeCandidate("source", "candidate", candidateTitle);

        var ranked = TitleMatcher.Rank(context, [candidate]);

        if (compatible)
            Assert.Same(candidate, Assert.Single(ranked));
        else
            Assert.Empty(ranked);
    }

    [Theory]
    [MemberData(nameof(SeasonAndPartCompatibility))]
    public void Confidence_RequiresAllSharedSeasonAndPartKindsToAgree(
        string title,
        string candidateTitle,
        bool compatible)
    {
        var context = new AnimePlaybackContext(42, title);
        var candidate = new SourceAnimeCandidate("source", "candidate", candidateTitle);

        Assert.Equal(compatible, TitleMatcher.IsConfident(context, candidate));
    }

    [Fact]
    public void Rank_UsesAlternateTitleAndNormalizesPunctuation()
    {
        var context = new AnimePlaybackContext(
            42,
            "葬送的芙莉莲",
            ["葬送のフリーレン"]);
        var candidates = new[]
        {
            new SourceAnimeCandidate("source", "wrong", "其他动画"),
            new SourceAnimeCandidate(
                "source",
                "match",
                "《葬送のフリーレン》"),
        };

        var ranked = TitleMatcher.Rank(context, candidates);

        Assert.Equal("match", Assert.Single(ranked).RemoteId);
        Assert.True(TitleMatcher.IsConfident(context, ranked[0]));
    }

    [Fact]
    public void GetSearchTitles_RemovesDuplicates()
    {
        var context = new AnimePlaybackContext(
            42,
            "Test",
            ["test", "テスト"]);

        var titles = TitleMatcher.GetSearchTitles(context);

        Assert.Equal(2, titles.Count);
    }

    [Fact]
    public void Rank_RejectsConflictingSeason()
    {
        var context = new AnimePlaybackContext(42, "Example Season 2");
        var candidates = new[]
        {
            new SourceAnimeCandidate("source", "season-1", "Example 第1季"),
            new SourceAnimeCandidate("source", "season-2", "Example 第2季"),
        };

        var ranked = TitleMatcher.Rank(context, candidates);

        Assert.Equal("season-2", Assert.Single(ranked).RemoteId);
    }
}
