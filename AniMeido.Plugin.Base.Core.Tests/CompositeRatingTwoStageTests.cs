using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class CompositeRatingTwoStageTests
{
    private static readonly DateOnly Today = new(2026, 11, 7);

    [Theory]
    [InlineData("whisper", 5.679968696156792)]
    [InlineData("slow-start", 6.945491116795607)]
    [InlineData("three-body", 2.2347843232054316)]
    [InlineData("heaven-official", 8.486272982431469)]
    [InlineData("link-click", 8.51739648797609)]
    [InlineData("penguin", 7.457948869560196)]
    public void ReviewedSamples_MatchTwoStageRawScores(string sample, double expected)
    {
        MetadataRating[] ratings = sample switch
        {
            "whisper" => [new("bangumi", 4.1, 2287), new("mal", 6.73, 26298),
                new("anilist", 65, 9479, "2026-11-07"), new("kitsu", 6.716517857142857, 448), new("ann", 6.8754, 41)],
            "slow-start" => [new("bangumi", 7, 2998), new("mal", 7.04, 31389),
                new("anilist", 68, 9482, "2026-11-07"), new("kitsu", 6.8524553571428575, 2240), new("ann", 6.9325, 79)],
            "three-body" => [new("bangumi", 1.7, 4762), new("mal", 6.44, 338),
                new("anilist", 60, 114, "2026-11-07"), new("kitsu", 5.8125, 16)],
            "heaven-official" => [new("bangumi", 5.6, 182), new("mal", 8.63, 26825),
                new("anilist", 86, 7860, "2026-11-07"), new("kitsu", 8.40648379052369, 401)],
            "link-click" => [new("bangumi", 5.1, 95), new("anilist", 86, 2019, "2026-11-07"), new("kitsu", 8.363636363636363, 44)],
            "penguin" => [new("bangumi", 7.4, 5432), new("mal", 7.53, 38683),
                new("anilist", 74, 12493, "2026-11-07"), new("kitsu", 7.62716894977169, 2190), new("ann", 7.5207, 107)],
            _ => throw new ArgumentOutOfRangeException(nameof(sample)),
        };
        Assert.Equal(expected, CompositeRating.Calculate(ratings, null, Today)!.Value.Score, 10);
    }

    [Theory]
    [InlineData("bangumi", 7.4, 141)]
    [InlineData("mal", 8.5, 500)]
    [InlineData("anilist", 85, 500)]
    public void SingleSource_KeepsRawScore(string platform, double score, int votes)
    {
        var result = CompositeRating.Calculate([new(platform, score, votes, "2026-11-07")], null, Today);
        Assert.Equal(platform == "anilist" ? score / 10 : score, result!.Value.Score, 12);
    }

    [Fact]
    public void EqualScores_StayEqualWithAnAdditionalSmallSource()
    {
        MetadataRating[] ratings = [new("bangumi", 8, 10000), new("mal", 8, 500)];
        Assert.Equal(8, CompositeRating.Calculate(ratings, null, Today)!.Value.Score, 12);
        Assert.Equal(8, CompositeRating.Calculate([.. ratings, new("kitsu", 8, 83)], null, Today)!.Value.Score, 12);
    }
}
