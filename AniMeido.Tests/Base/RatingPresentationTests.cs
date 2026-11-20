using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class RatingPresentationTests
{
    private static readonly DateOnly Today = new(2026, 10, 21);

    [Fact]
    public void Rows_KeepSourceOrderOriginalScalesAndVotes()
    {
        MetadataRating[] ratings = [new("ann", 7.12, 41), new("anilist", 71, 15936, "2026-10-06"),
            new("mal", 7.52, 55184), new("bangumi", 7.5, 9114)];

        var rows = RatingPresentation.CreateRows(ratings, Today);

        Assert.Collection(rows,
            row => Assert.Equal(("Bangumi", "7.5", "10", "9,114"), (row.Source, row.Score, row.Scale, row.Votes)),
            row => Assert.Equal(("MyAnimeList", "7.52", "10", "55,184"), (row.Source, row.Score, row.Scale, row.Votes)),
            row => Assert.Equal(("AniList", "71", "100", "15,936"), (row.Source, row.Score, row.Scale, row.Votes)),
            row => Assert.Equal(("Anime News Network", "7.12", "10", "41"), (row.Source, row.Score, row.Scale, row.Votes)));
    }

    [Fact]
    public void Rows_HideUnavailableScoresAndKeepSmallValidSamples()
    {
        MetadataRating[] ratings = [new("bangumi", 7.5, 49), new("anilist", 99, 900, "2026-10-05"),
            new("mal", null, 1000), new("kitsu", 8, 0), new("ann", 8, null)];

        var row = Assert.Single(RatingPresentation.CreateRows(ratings, Today));
        Assert.Equal("Bangumi", row.Source);
        Assert.Equal("49", row.Votes);
        Assert.Empty(RatingPresentation.CreateRows([], Today));
    }

    [Fact]
    public void Presentation_PreservesPlatformScalesOrderAndExactFormatting()
    {
        MetadataRating[] ratings = [new("ann", 7.12, 41), new("kitsu", 7.58, 336), new("anilist", 71, 15936, "2026-10-06"),
            new("mal", 7.52, 55184), new("bangumi", 7.5, 9114)];
        Assert.Equal(string.Join(Environment.NewLine,
            "综合评分　7.4", "Bangumi　7.5 分（9,114 人评分）", "MyAnimeList　7.52 分（55,184 人评分）",
            "AniList　71 分（满分 100，15,936 人评分）", "Kitsu　7.58 分（336 人评分）", "Anime News Network　7.12 分（41 人评分）"),
            RatingPresentation.Format(ratings, null, Today));
    }

    [Fact]
    public void Presentation_HidesOnlyUnavailableRowsAndKeepsSmallSamplePlatformScores()
    {
        MetadataRating[] ratings = [new("bangumi", 7.5, 49), new("anilist", 99, 900, "2026-10-05"),
            new("anilist", 98, 800), new("anilist", 99, 900, "2026-10-22"), new("anilist", 99, 900, "invalid"), new("mal", null, 1000), new("kitsu", 8, 0), new("ann", 8, null)];
        Assert.Equal("Bangumi　7.5 分（49 人评分）", RatingPresentation.Format(ratings, null, Today));
        Assert.Equal("Bangumi　8.0 分（100 人评分）", RatingPresentation.Format([new("bangumi", 8, 100)], "2027", Today));
        Assert.Empty(RatingPresentation.Format([], null, Today));
        Assert.Empty(RatingPresentation.Format([new("anilist", 80, 100)], null, Today));
    }
    [Theory]
    [InlineData(50, "综合评分　7.4（评分人数较少）")]
    [InlineData(299, "综合评分　7.4（评分人数较少）")]
    [InlineData(300, "综合评分　7.4")]
    public void LowVotes_ShowRawCompositeAndSampleNotice(int votes, string expected)
    {
        Assert.Equal(expected, RatingPresentation.Format([new("bangumi", 7.4, votes)], null, Today)
            .Split(Environment.NewLine)[0]);
    }
    [Fact]
    public void Explanation_IdentifiesVersionThresholdAndFeedbackInputs()
    {
        Assert.Contains(CompositeRating.ParameterVersion, RatingPresentation.Explanation);
        Assert.Contains("300", RatingPresentation.Explanation);
        Assert.Contains("50", RatingPresentation.Explanation);
        Assert.Contains("评分截图", RatingPresentation.Explanation);
    }
}
