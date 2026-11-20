using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class RecommendationCorrectnessTests
{
    private static readonly DateOnly Today = new(2026, 11, 11);
    private static RecommendationFeature Tag(string key) => new(RecommendationFeatureKind.Tag, key, key);
    private static RecommendationFeatureProfile Profile(string key, double score,
        RecommendationAdjustment? adjustment = null, bool saved = false)
        => new(Tag(key), score, adjustment, [], saved);
    private static RecommendationCandidate Candidate(int id, params RecommendationFeature[] features)
        => new(new Anime(id, $"作品{id}", null, [], new(2026, 1, 1), null, "", 2026, 1, Score: 8), features);

    [Fact]
    public void AddingSavedMatchNeverLowersScore()
    {
        RecommendationFeatureProfile[] profile = [Profile("A", 10), Profile("B", 8), Profile("C", 6), Profile("D", 2.5, saved: true)];
        var results = RecommendationScorer.Rank(profile,
            [Candidate(1, Tag("A"), Tag("B"), Tag("C")), Candidate(2, Tag("A"), Tag("B"), Tag("C"), Tag("D"))], Today);
        Assert.Equal(results.Single(x => x.Anime.ID == 1).Score, results.Single(x => x.Anime.ID == 2).Score);
    }

    [Fact]
    public void EveryManualReductionCountsAlongsideAutomaticReductions()
    {
        RecommendationFeatureProfile[] profile = [Profile("A", 24), Profile("N1", -8), Profile("N2", -7),
            Profile("R", 0, RecommendationAdjustment.Reduce), Profile("R2", 0, RecommendationAdjustment.Reduce)];
        var result = RecommendationScorer.Rank(profile,
            [Candidate(1, Tag("A"), Tag("N1"), Tag("N2")), Candidate(2, Tag("A"), Tag("N1"), Tag("N2"), Tag("R"), Tag("R2"), Tag("R"))], Today);
        Assert.Equal(result.Single(x => x.Anime.ID == 1).Score - 12, result.Single(x => x.Anime.ID == 2).Score);
        Assert.Contains(result.Single(x => x.Anime.ID == 2).Reasons, x => x.IsReduction && x.Feature.Key == "R");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PositiveAlternativesWinOverDiversityAndAgeQuota(bool negativeIsClassic)
    {
        RecommendationFeatureProfile[] profile = [Profile("A", 10), Profile("B", 1), Profile("R", 0, RecommendationAdjustment.Reduce)];
        var negative = Candidate(99, Tag("B"), Tag("R"));
        if (negativeIsClassic) negative = negative with { Anime = negative.Anime with { AirDate = new DateOnly(2000, 1, 1) } };
        var result = RecommendationScorer.Rank(profile,
            Enumerable.Range(1, 20).Select(id => Candidate(id, Tag("A"))).Append(negative).ToArray(), Today);
        Assert.Equal(20, result.Count);
        Assert.DoesNotContain(result, x => x.Anime.ID == 99);
    }

    [Fact]
    public void ExplorationDoesNotRewardLosingRelevantMetadata()
    {
        RecommendationFeatureProfile[] profile = [.. Enumerable.Range(1, 13).Select(id => Profile($"A{id}", 10)),
            Profile("B", 2), Profile("C", 2), Profile("W", 0.3), Profile("OLD", 8)];
        var candidates = Enumerable.Range(1, 13).Select(id => Candidate(id, Tag($"A{id}"))).ToList();
        candidates.Add(Candidate(90, Tag("W")));
        candidates.Add(Candidate(91, Tag("B"), Tag("C")));
        candidates.AddRange(Enumerable.Range(100, 6).Select(id => Candidate(id, Tag("OLD")) with
        { Anime = Candidate(id).Anime with { AirDate = new DateOnly(2000, 1, 1) } }));
        var result = RecommendationScorer.Rank(profile, candidates, Today);
        Assert.Contains(result, x => x.Anime.ID == 91);
        Assert.DoesNotContain(result, x => x.Anime.ID == 90);
        var reduced = candidates.Select(candidate => candidate.Anime.ID == 91
            ? candidate with { Features = [Tag("B")] } : candidate).ToArray();
        var after = RecommendationScorer.Rank(profile, reduced, Today);
        Assert.True(after.Single(x => x.Anime.ID == 91).Score < result.Single(x => x.Anime.ID == 91).Score);
        Assert.True(after.ToList().FindIndex(x => x.Anime.ID == 91) >= result.ToList().FindIndex(x => x.Anime.ID == 91));
    }

    [Fact]
    public void FeatureAndStudioOrderDoNotChangeSelection()
    {
        RecommendationFeatureProfile[] profile = [Profile("A", 6), Profile("B", 6)];
        var studios = new[] { new RecommendationFeature(RecommendationFeatureKind.Studio, "1", "一"),
            new RecommendationFeature(RecommendationFeatureKind.Studio, "2", "二") };
        var candidates = Enumerable.Range(1, 12).Select(id => Candidate(id,
            [Tag(id > 6 ? "B" : "A"), Tag(id > 6 ? "A" : "B"), .. studios])).ToArray();
        var expected = RecommendationScorer.Rank(profile, candidates, Today);
        var actual = RecommendationScorer.Rank(profile.Reverse().ToArray(), candidates.Select(x => x with
            { Features = x.Features.Reverse().ToArray() }).Reverse().ToArray(), Today);
        Assert.Equal(expected.Select(x => (x.Anime.ID, x.Score, x.PrimaryReason)), actual.Select(x => (x.Anime.ID, x.Score, x.PrimaryReason)));
    }

    [Fact]
    public void EvidenceExplainsTheNetDirectionOfEachWork()
    {
        var profile = Assert.Single(RecommendationScorer.BuildProfile(
            [new(1, "作品", -1, [new(RecommendationEvidenceSource.PersonalRating, 1), new(RecommendationEvidenceSource.Dropped, -2)])],
            new Dictionary<int, IReadOnlyList<RecommendationFeature>> { [1] = [Tag("A")] }, []));
        var evidence = Assert.Single(profile.Evidence);
        Assert.Equal(-1, evidence.Contribution);
        Assert.Equal(RecommendationEvidenceSource.Dropped, evidence.Source);
    }

    [Fact]
    public void EveryStudioInACollaborationContributesToDiversity()
    {
        var profile = Enumerable.Range(1, 6).Select(id => Profile($"A{id}", 10 - id * 0.1)).ToArray();
        var shared = new RecommendationFeature(RecommendationFeatureKind.Studio, "shared", "共同制作方");
        var candidates = Enumerable.Range(1, 6).Select(id => Candidate(id, id <= 5
            ? [Tag($"A{id}"), new(RecommendationFeatureKind.Studio, $"{id}", "另一制作方"), shared]
            : [Tag($"A{id}")])).ToArray();
        var result = RecommendationScorer.Rank(profile, candidates, Today);
        Assert.Equal(new[] { 1, 2, 3, 4, 6, 5 }, result.Select(item => item.Anime.ID));
    }

    [Fact]
    public void UnknownDateAndInferredInterestUseAccurateLabels()
    {
        var profile = Profile("A", 2);
        Assert.Equal("推测感兴趣", profile.DirectionText);
        var item = Assert.Single(RecommendationScorer.Rank([profile],
            [Candidate(1, Tag("A")) with { Anime = Candidate(1).Anime with { AirDate = null } }], Today));
        Assert.Contains("年代未知", item.Metadata);
    }
}
