using System.Diagnostics;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Xunit.Abstractions;

namespace AniMeido.Tests;

public sealed class RecommendationRankingEvaluationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(30)]
    [InlineData(120)]
    [InlineData(1000)]
    [Trait("Category", "Performance")]
    public void MixedCandidatePoolsKeepQuotasStableOrderAndBoundedRankingCost(int count)
    {
        var today = new DateOnly(2026, 11, 11);
        var features = Enumerable.Range(0, 8)
            .Select(i => new RecommendationFeature(RecommendationFeatureKind.Tag, $"TAG-{i}", $"题材{i}")).ToArray();
        var studios = Enumerable.Range(0, 7).Select(i => new RecommendationFeature(RecommendationFeatureKind.Studio, $"{i}", $"制作方{i}")).ToArray();
        var actors = Enumerable.Range(0, 5).Select(i => new RecommendationFeature(RecommendationFeatureKind.VoiceActor, $"{i}", $"声优{i}")).ToArray();
        var reduced = new RecommendationFeature(RecommendationFeatureKind.Tag, "REDUCE", "减少项");
        var profile = features.Select((feature, i) => new RecommendationFeatureProfile(
            feature, 4 - i * .1, null, [])).Concat(studios.Select(feature => new RecommendationFeatureProfile(feature, 1, null, [])))
            .Concat(actors.Select(feature => new RecommendationFeatureProfile(feature, 0.5, null, [])))
            .Append(new(reduced, 0, RecommendationAdjustment.Reduce, [])).ToArray();
        var candidates = Enumerable.Range(1, count).Select(id => new RecommendationCandidate(
            new Anime(id, $"合成作品{id}", null, [], id % 3 == 0 ? new(2010, 1, 1) : new(2026, 1, 1),
                null, "", 2026, 1, Score: 6 + id % 30 * .1),
            [features[id % 8], features[(id + 1) % 8], features[(id + 2) % 8], studios[id % 7], studios[(id + 1) % 7],
                actors[id % 5], actors[(id + 1) % 5], .. id % 5 == 0 ? new[] { reduced } : []])).ToArray();
        var expected = RecommendationScorer.Rank(profile, candidates, today);
        Assert.Equal(20, expected.Count);
        Assert.Equal(14, expected.Count(item => item.IsRecent));
        Assert.Equal(6, expected.Count(item => !item.IsRecent));
        Assert.Equal(20, expected.Select(item => item.Anime.ID).Distinct().Count());
        Assert.Equal(expected.Select(item => item.Anime.ID), RecommendationScorer.Rank(
            profile, candidates.Reverse().ToArray(), today).Select(item => item.Anime.ID));
        for (var i = 0; i < 5; i++) RecommendationScorer.Rank(profile, candidates, today);

        const int rounds = 50;
        var durations = new double[rounds];
        var allocations = new long[rounds];
        for (var i = 0; i < rounds; i++)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            var actual = RecommendationScorer.Rank(profile, candidates, today);
            durations[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocations[i] = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Assert.Equal(expected.Select(item => item.Anime.ID), actual.Select(item => item.Anime.ID));
        }
        output.WriteLine("Candidates={0}; Rounds={1}; MedianMs={2:F3}; P95Ms={3:F3}; MedianBytes={4}",
            count, rounds, durations.Order().ElementAt(25), durations.Order().ElementAt(47),
            allocations.Order().ElementAt(25));
        output.WriteLine("Synthetic data; actual scorer; warmed process; no network or database; setup/assertions excluded.");
    }
}
