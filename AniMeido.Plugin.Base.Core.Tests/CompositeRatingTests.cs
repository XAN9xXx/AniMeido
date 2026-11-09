using AniMeido.Plugin.Base.Services;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class CompositeRatingTests
{
    private static readonly DateOnly Today = new(2026, 10, 21);

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

    [Theory]
    [InlineData(49, false, false)]
    [InlineData(50, false, true)]
    [InlineData(299, false, true)]
    [InlineData(300, true, true)]
    public void Qualification_UsesMaximumAndDoesNotChangeScore(int votes, bool normal, bool lowVotes)
    {
        MetadataRating[] ratings = [new("bangumi", 8, votes), new("mal", 8, votes)];
        var result = CompositeRating.Calculate(ratings, null, Today)!.Value;
        Assert.Equal(8, result.Score, 12);
        Assert.Equal(normal, result.IsEligible());
        Assert.Equal(lowVotes, result.IsEligible(includeLowVotes: true));
        Assert.Equal(normal, CompositeRating.ScoreForRanking(ratings, null, Today).HasValue);
        Assert.Equal(lowVotes, CompositeRating.ScoreForRanking(ratings, null, Today, true).HasValue);
        Assert.Equal("2026-11-08", CompositeRating.ParameterVersion);
    }

    [Fact]
    public void ExpiredLargestSource_RecomputesScoreStrengthAndEligibility()
    {
        MetadataRating[] ratings = [new("bangumi", 6, 299), new("anilist", 90, 300, "2026-10-06"), new("mal", 7, 40)];
        var before = CompositeRating.Calculate(ratings, null, Today)!.Value;
        var after = CompositeRating.Calculate(ratings, null, Today.AddDays(1))!.Value;
        Assert.True(before.IsEligible());
        Assert.False(after.IsEligible());
        Assert.True(after.IsEligible(true));
        Assert.Equal(299, after.MaximumVotes);
        Assert.Equal("anilist", before.StrongestOverseasPlatform);
        Assert.Equal("mal", after.StrongestOverseasPlatform);
        Assert.Equal(CompositeRating.Calculate([ratings[0], ratings[2]], null, Today), after);
        Assert.NotEqual(before.ChineseShare, after.ChineseShare);
    }

    [Fact]
    public void StrongestSource_HandlesTiesCrossoverAndRemoval()
    {
        CompositeRatingResult Result(long malVotes) => CompositeRating.Calculate([
            new("bangumi", 7, 10000), new("mal", 8, malVotes), new("anilist", 60, 10000, "2026-10-21")], null, Today)!.Value;
        var left = Result(9999);
        var tie = Result(10000);
        var right = Result(10001);
        Assert.Equal("anilist", left.StrongestOverseasPlatform);
        Assert.Equal("mal", tie.StrongestOverseasPlatform);
        Assert.Equal("mal", right.StrongestOverseasPlatform);
        Assert.InRange(Math.Abs(left.Score - right.Score), 0, 0.0001);
        var remaining = CompositeRating.Calculate([new("bangumi", 7, 10000), new("anilist", 60, 10000, "2026-10-21")], null, Today)!.Value;
        Assert.Equal("anilist", remaining.StrongestOverseasPlatform);
        Assert.Equal(tie.ChineseShare, remaining.ChineseShare);
    }

    [Fact]
    public void AddedSmallCommunity_EntersWithSmallInfluence()
    {
        var before = CompositeRating.Calculate([new("mal", 7, 10000)], null, Today)!.Value;
        var after = CompositeRating.Calculate([new("mal", 7, 10000), new("bangumi", 10, 1)], null, Today)!.Value;
        Assert.InRange(after.Score - before.Score, 0, 0.00001);
    }

    [Fact]
    public void MoreVotesBelowGroupMean_LowersGroupMeanWithoutChangingGroupShare()
    {
        CompositeRatingResult Result(long votes) => CompositeRating.Calculate([
            new("bangumi", 4, 10000), new("mal", 8, 10000), new("anilist", 60, votes, "2026-10-21")], null, Today)!.Value;
        var before = Result(1000);
        var after = Result(2000);
        Assert.Equal(5.7260334864, before.Score, 10);
        Assert.Equal(5.6206401313, after.Score, 10);
        Assert.Equal(before.ChineseShare, after.ChineseShare);
        Assert.True(after.OverseasScore < before.OverseasScore);
    }

    [Fact]
    public void MoreVotesAboveBothMeans_CanLowerTotalByIncreasingLowerGroupShare()
    {
        CompositeRatingResult Result(long votes) => CompositeRating.Calculate([
            new("bangumi", 10, 10000), new("mal", 8, votes), new("anilist", 10, 9000, "2026-10-21")], null, Today)!.Value;
        var before = Result(10000);
        var after = Result(20000);
        Assert.True(8 > before.Score);
        Assert.True(after.OverseasScore > before.OverseasScore);
        Assert.True(after.Score < before.Score);
        var withinGroup = (1 - before.ChineseShare) * (after.OverseasScore!.Value - before.OverseasScore!.Value);
        var betweenGroups = (before.ChineseShare - after.ChineseShare) * (after.OverseasScore.Value - 10);
        Assert.True(withinGroup > 0);
        Assert.True(betweenGroups < 0);
        Assert.Equal(after.Score - before.Score, withinGroup + betweenGroups, 12);
    }

    [Fact]
    public void EverySourceSubset_PreservesRangeMonotonicityAndOrderIndependence()
    {
        string[] platforms = ["bangumi", "mal", "anilist", "kitsu", "ann"];
        var random = new Random(20261108);
        for (var mask = 1; mask < 32; mask++)
        for (var trial = 0; trial < 100; trial++)
        {
            var ratings = platforms.Where((_, index) => (mask & (1 << index)) != 0)
                .Select(p => new MetadataRating(p, (1 + random.NextDouble() * 8) * (p == "anilist" ? 10 : 1),
                    random.Next(1, 100001), "2026-10-21")).ToArray();
            var score = CompositeRating.Calculate(ratings, null, Today)!.Value.Score;
            var raw = ratings.Select(r => r.Score!.Value / (r.Platform == "anilist" ? 10 : 1)).ToArray();
            Assert.InRange(score, raw.Min() - 1e-12, raw.Max() + 1e-12);
            Assert.Equal(score, CompositeRating.Calculate(ratings.Reverse(), null, Today)!.Value.Score, 10);
            for (var i = 0; i < ratings.Length; i++)
            {
                var raised = ratings.ToArray();
                raised[i] = ratings[i] with { Score = ratings[i].Score + (ratings[i].Platform == "anilist" ? 1 : 0.1) };
                Assert.True(CompositeRating.Calculate(raised, null, Today)!.Value.Score > score);
            }
        }
    }

    [Theory]
    [InlineData("bangumi", 0)]
    [InlineData("mal", 10.1)]
    [InlineData("kitsu", -1)]
    [InlineData("anilist", 101)]
    public void ScoresOutsidePlatformScale_AreExcluded(string platform, double score)
    {
        Assert.Null(CompositeRating.Calculate([new(platform, score, 1000, "2026-10-21")], null, Today));
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
