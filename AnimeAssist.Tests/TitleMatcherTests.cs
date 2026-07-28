using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Player.Sources;

namespace AniMeido.Tests;

public sealed class TitleMatcherTests
{
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
