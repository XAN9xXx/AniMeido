using AniMeido.Plugin.Base.Services;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class CompositeRatingTests
{
    private static readonly DateOnly Today = new(2026, 10, 21);

    [Theory]
    [InlineData("small-bangumi", 7.241, 0.6)]
    [InlineData("large-bangumi", 7.983, 0.6)]
    [InlineData("higher-overseas", 7.204, 0.4)]
    [InlineData("switch-left", 7.912, 0.4)]
    [InlineData("switch-equal", 7.914, 0.6)]
    [InlineData("higher-chinese", 7.319, 0.6)]
    [InlineData("uneven-votes", 7.121, 0.6)]
    [InlineData("mal-only", 7.705, 0.6)]
    [InlineData("overseas-only", 7.611, 0.6)]
    [InlineData("below-threshold", null, null)]
    [InlineData("swapped-votes", 7.914, 0.6)]
    [InlineData("bangumi-100", 7.443, 0.6)]
    [InlineData("anilist-day15", 7.787, 0.6)]
    [InlineData("anilist-day16", 7.443, 0.6)]
    [InlineData("anilist-missing-date", 7.443, 0.6)]
    [InlineData("anilist-future-date", 7.443, 0.6)]
    public void DesignExamples_MatchWithinOneThousandth(string example, double? expected, double? alpha)
    {
        MetadataRating[] ratings = example switch
        {
            "switch-left" => [new("bangumi", 7.99, 100), new("mal", 8.657, 100000)],
            "switch-equal" => [new("bangumi", 8, 100), new("mal", 8.657, 100000)],
            "swapped-votes" => [new("bangumi", 8, 100000), new("mal", 8.657, 100)],
            "bangumi-100" => [new("bangumi", 8, 100)],
            "anilist-day15" => [new("bangumi", 8, 100), new("anilist", 83.53, 1000, "2026-10-06")],
            "anilist-day16" => [new("bangumi", 8, 100), new("anilist", 83.53, 1000, "2026-10-05")],
            "anilist-missing-date" => [new("bangumi", 8, 100), new("anilist", 83.53, 1000)],
            "anilist-future-date" => [new("bangumi", 8, 100), new("anilist", 83.53, 1000, "2026-10-22")],
            "small-bangumi" => [new("bangumi", 8, 60)],
            "large-bangumi" => [new("bangumi", 8, 5000)],
            "higher-overseas" => [new("bangumi", 7, 3000), new("mal", 8, 100000)],
            "higher-chinese" => [new("bangumi", 8, 3000), new("mal", 7, 100000)],
            "uneven-votes" => [new("bangumi", 9, 60), new("mal", 7, 50000)],
            "mal-only" => [new("mal", 8.5, 500)],
            "overseas-only" => [new("mal", 8.5, 500), new("kitsu", 8.5, 83)],
            "below-threshold" => [new("bangumi", 7.5, 40), new("anilist", 80, 45, "2026-10-21")],
            _ => throw new ArgumentOutOfRangeException(nameof(example)),
        };
        var result = CompositeRating.Calculate(ratings, null, Today);
        if (expected is null) Assert.Null(result);
        else
        {
            Assert.NotNull(result);
            Assert.True(Math.Abs(expected.Value - result.Value.Score) < 0.001);
            Assert.Equal(alpha, result.Value.NominalChineseShare);
        }
        Assert.Equal("2026-10-21", CompositeRating.ParameterVersion);
    }

    [Theory]
    [InlineData("2026-10-21", true)]
    [InlineData("2026-10-06", true)]
    [InlineData("2026-10-05", false)]
    [InlineData("2026-10-22", false)]
    [InlineData("2026-02-30", false)]
    [InlineData(null, false)]
    [InlineData("invalid", false)]
    public void AniListFreshness_RecomputesAllWeights(string? fetched, bool included)
    {
        MetadataRating[] without = [new("bangumi", 6, 100), new("mal", 8, 900)];
        var result = CompositeRating.Calculate([.. without, new("anilist", 98, 10000, fetched)], null, Today);
        var baseline = CompositeRating.Calculate(without, null, Today);
        if (included) Assert.NotEqual(baseline, result);
        else Assert.Equal(baseline, result);
        Assert.Equal(included, CompositeRating.Calculate([new("anilist", 80, 50, fetched)], null, Today).HasValue);
    }

    [Theory]
    [InlineData("2026-10-22", false)]
    [InlineData("2026-10-21", true)]
    [InlineData("2026-11", false)]
    [InlineData("2026-10", true)]
    [InlineData("2027", false)]
    [InlineData("2026", true)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("invalid", true)]
    public void FirstDate_UsesPartialDateStartOrTreatsUnknownAsAired(string? first, bool eligible) =>
        Assert.Equal(eligible, CompositeRating.Calculate([new("bangumi", 8, 50)], first, Today).HasValue);

    [Theory]
    [InlineData(49, false)]
    [InlineData(50, true)]
    public void Threshold_UsesMaximumNotSum(int votes, bool eligible) =>
        Assert.Equal(eligible, CompositeRating.Calculate([new("bangumi", 8, votes), new("mal", 8, votes)], null, Today).HasValue);

    [Fact]
    public void InvalidOrUnknownRatings_DoNotAffectResult()
    {
        MetadataRating[] valid = [new("bangumi", 8, 60)];
        MetadataRating[] invalid = [new("unknown", 10, 100000), new("mal", null, 1000), new("kitsu", 9, 0),
            new("ann", 9, null), new("anilist", 100, -1, "2026-10-21"), new("mal", double.NaN, 1000),
            new("mal", double.PositiveInfinity, 1000), new("ann", double.NegativeInfinity, 1000)];
        Assert.Equal(CompositeRating.Calculate(valid, null, Today), CompositeRating.Calculate([.. valid, .. invalid], null, Today));
        Assert.Null(CompositeRating.Calculate(invalid, null, Today));
        Assert.Null(CompositeRating.Calculate([], null, Today));
    }

    [Fact]
    public void EqualShiftedScoresAndEqualVotes_HaveNoJumpAtShareSwitch()
    {
        var equal = CompositeRating.Calculate([new("bangumi", 8, 100), new("mal", 8.657, 100)], null, Today)!.Value;
        var lower = CompositeRating.Calculate([new("bangumi", 8 - 1e-9, 100), new("mal", 8.657, 100)], null, Today)!.Value;
        var higher = CompositeRating.Calculate([new("bangumi", 8 + 1e-9, 100), new("mal", 8.657, 100)], null, Today)!.Value;
        Assert.Equal(0.4, lower.NominalChineseShare);
        Assert.Equal(0.6, equal.NominalChineseShare);
        Assert.Equal(0.6, higher.NominalChineseShare);
        Assert.Equal(6.33 + 100.0 / 150 * (8 - 6.33), equal.Score, 10);
        Assert.True(Math.Abs(higher.Score - lower.Score) < 1e-8);
    }

    [Theory]
    [InlineData(50, true)]
    [InlineData(10000, true)]
    [InlineData(100000, true)]
    [InlineData(50, false)]
    [InlineData(10000, false)]
    [InlineData(100000, false)]
    public void FixedVotes_ScanningEitherPlatformAcrossSwitchIsContinuousAndNondecreasing(int malVotes, bool varyBangumi)
    {
        double previous = double.NegativeInfinity;
        for (var step = -100; step <= 100; step++)
        {
            var delta = step * 0.0001;
            var result = CompositeRating.Calculate([
                new("bangumi", 8 + (varyBangumi ? delta : 0), 100),
                new("mal", 8.657 + (varyBangumi ? 0 : delta), malVotes)], null, Today)!.Value;
            Assert.True(result.Score >= previous);
            if (step > -100) Assert.InRange(result.Score - previous, 0, 0.000101);
            previous = result.Score;
        }
    }

    [Fact]
    public void EqualScores_SwappingGroupCountsDoesNotBiasReferenceVotes()
    {
        var chineseSmall = CompositeRating.Calculate([new("bangumi", 8, 100), new("mal", 8.657, 100000)], null, Today)!.Value;
        var overseasSmall = CompositeRating.Calculate([new("bangumi", 8, 100000), new("mal", 8.657, 100)], null, Today)!.Value;
        Assert.Equal(chineseSmall.Score, overseasSmall.Score, 12);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(10000)]
    public void EqualCounts_MorePlatformsDoNotIncreaseReferenceVotes(int votes)
    {
        MetadataRating[] ratings = [new("bangumi", 8, votes), new("mal", 8.657, votes),
            new("anilist", 83.53, votes, "2026-10-21"), new("kitsu", 8.522, votes), new("ann", 8.456, votes)];
        var expected = 6.33 + votes / (votes + 50.0) * (8 - 6.33);
        for (var count = 1; count <= ratings.Length; count++)
            Assert.Equal(expected, CompositeRating.Calculate(ratings.Take(count), null, Today)!.Value.Score, 12);
    }

    [Fact]
    public void MoreBangumiVotes_CanDecreaseScoreAsSpecified()
    {
        var before = CompositeRating.Calculate([new("bangumi", 8.10, 10), new("mal", 8.660, 1000)], null, Today)!.Value;
        var after = CompositeRating.Calculate([new("bangumi", 8.10, 50), new("mal", 8.660, 1000)], null, Today)!.Value;
        Assert.InRange(Math.Abs(before.Score - 7.883), 0, 0.001);
        Assert.InRange(Math.Abs(after.Score - 7.805), 0, 0.001);
        Assert.True(after.Score < before.Score);
    }

    [Fact]
    public void MoreKitsuVotes_CanDecreaseScoreAsSpecified()
    {
        MetadataRating[] fixedRatings = [new("bangumi", 8, 10000), new("mal", 8.660, 50),
            new("anilist", 84, 50, "2026-10-21"), new("ann", 8.460, 50)];
        var before = CompositeRating.Calculate([.. fixedRatings, new("kitsu", 8.620, 50)], null, Today)!.Value;
        var after = CompositeRating.Calculate([.. fixedRatings, new("kitsu", 8.620, 100)], null, Today)!.Value;
        Assert.InRange(Math.Abs(before.Score - 7.906), 0, 0.001);
        Assert.InRange(Math.Abs(after.Score - 7.858), 0, 0.001);
        Assert.True(after.Score < before.Score);
    }

    [Theory]
    [InlineData("50.5")]
    [InlineData("null")]
    [InlineData("-50")]
    [InlineData("1e30")]
    public void InvalidSerializedVotes_ExcludeOnlyThatRating(string votes)
    {
        var json = "[{\"platform\":\"bangumi\",\"score\":8,\"votes\":100},{\"platform\":\"mal\",\"score\":10,\"votes\":" + votes + "}]";
        var ratings = JsonSerializer.Deserialize<MetadataRating[]>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(CompositeRating.Calculate([new("bangumi", 8, 100)], null, Today), CompositeRating.Calculate(ratings, null, Today));
    }

    [Fact]
    public void SerializedIntegerVotes_AcceptIntegralNumbersAndRoundTrip()
    {
        const string json = """[{"platform":"bangumi","score":8,"votes":100.0},{"platform":"mal","score":8,"votes":3000000000}]""";
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var ratings = JsonSerializer.Deserialize<MetadataRating[]>(json, options)!;
        Assert.Equal(100L, ratings[0].Votes);
        Assert.Equal(3000000000L, ratings[1].Votes);
        Assert.Equal(ratings, JsonSerializer.Deserialize<MetadataRating[]>(JsonSerializer.Serialize(ratings, options), options));
    }
}
