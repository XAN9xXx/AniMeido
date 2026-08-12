using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public class RecommendationScorerTests
{
    [Fact]
    public void BuildProfile_NormalizesFeaturesAndAppliesManualPreference()
    {
        var scienceFiction = Feature(
            RecommendationFeatureKind.Tag,
            "SCI-FI",
            "科幻");
        var adventure = Feature(
            RecommendationFeatureKind.Tag,
            "ADVENTURE",
            "冒险");
        var studio = Feature(
            RecommendationFeatureKind.Studio,
            "100",
            "测试制作方");
        var features = new Dictionary<
            int,
            IReadOnlyList<RecommendationFeature>>
        {
            [1] = [scienceFiction, adventure, studio],
        };
        var preferences = new[]
        {
            new RecommendationFeaturePreference(
                RecommendationFeatureKind.Tag,
                "sci-fi",
                "科幻",
                RecommendationAdjustment.Like,
                DateTimeOffset.UtcNow),
        };

        var profile = RecommendationScorer.BuildProfile(
            [new RecommendationSeed(1, "种子番剧", 4)],
            features,
            preferences);

        var tag = Assert.Single(profile, item => item.Feature.Key == "SCI-FI");
        Assert.Equal(4 / Math.Sqrt(2), tag.InferredScore, precision: 6);
        Assert.Equal(
            4 / Math.Sqrt(2) + 6,
            tag.EffectiveScore,
            precision: 6);
        Assert.Equal("种子番剧", Assert.Single(tag.Evidence).Title);
        Assert.Equal(
            4,
            Assert.Single(profile, item => item.Feature.Kind
                == RecommendationFeatureKind.Studio).InferredScore,
            precision: 6);
    }

    [Fact]
    public void Rank_UsesRecentClassicQuotaAndExplainsManualReduction()
    {
        var liked = Feature(
            RecommendationFeatureKind.Tag,
            "LIKED",
            "喜欢的标签");
        var reduced = Feature(
            RecommendationFeatureKind.Studio,
            "9",
            "降低的制作方");
        var profile = new[]
        {
            new RecommendationFeatureProfile(
                liked,
                2,
                null,
                [new RecommendationEvidence(1, "证据番剧", 2)]),
            new RecommendationFeatureProfile(
                reduced,
                0,
                RecommendationAdjustment.Reduce,
                []),
        };
        var today = new DateOnly(2026, 8, 1);
        var candidates = Enumerable.Range(1, 25)
            .Select(index => new RecommendationCandidate(
                Anime(
                    index,
                    index <= 14
                        ? today.AddYears(-1)
                        : today.AddYears(-8),
                    8),
                index == 1 ? [liked, reduced] : [liked],
                0))
            .ToArray();

        var result = RecommendationScorer.Rank(
            profile,
            candidates,
            today);

        Assert.Equal(20, result.Count);
        Assert.Equal(14, result.Count(item => item.IsRecent));
        Assert.Equal(6, result.Count(item => !item.IsRecent));
        var first = Assert.Single(result, item => item.Anime.ID == 1);
        Assert.Contains(first.Reasons, reason => reason.IsReduction);
        Assert.Contains(first.Reasons, reason => reason.Text.Contains("证据番剧"));
    }

    [Fact]
    public void BuildProfile_IncludesSavedBangumiTagAsMediumSignal()
    {
        var profile = RecommendationScorer.BuildProfile(
            [],
            new Dictionary<int, IReadOnlyList<RecommendationFeature>>(),
            [],
            ["科幻"]);

        var savedTag = Assert.Single(profile);
        Assert.True(savedTag.IsSavedTag);
        Assert.Equal(2.5, savedTag.EffectiveScore);
        Assert.Equal("来自收藏 Tag", savedTag.DirectionText);

        var result = RecommendationScorer.Rank(
            profile,
            [new RecommendationCandidate(
                Anime(99, new DateOnly(2026, 1, 1), 8),
                [savedTag.Feature],
                0)],
            new DateOnly(2026, 8, 1));
        Assert.Contains("收藏了 Tag", Assert.Single(result).ReasonSummary);
    }

    [Fact]
    public void Rank_PrioritizesSavedTagInScoreAndExplanation()
    {
        var savedTag = new RecommendationFeatureProfile(
            Feature(RecommendationFeatureKind.Tag, "百合", "百合"),
            2.5,
            null,
            [],
            IsSavedTag: true);
        var inferred = new[] { 10d, 8d, 6d }
            .Select((score, index) => new RecommendationFeatureProfile(
                Feature(
                    RecommendationFeatureKind.Tag,
                    $"INFERRED-{index}",
                    $"推断 {index}"),
                score,
                null,
                [new RecommendationEvidence(1, "证据番剧", score)]))
            .ToArray();
        var profile = inferred.Append(savedTag).ToArray();
        var result = RecommendationScorer.Rank(
            profile,
            [new RecommendationCandidate(
                Anime(101, new DateOnly(2026, 1, 1), 8),
                profile.Select(item => item.Feature).ToArray(),
                0)],
            new DateOnly(2026, 8, 1));

        var item = Assert.Single(result);
        Assert.Contains("收藏了 Tag“百合”", item.ReasonSummary);
        Assert.True(item.Score >= 20.5);
    }

    [Fact]
    public void Rank_RejectsCandidateWithOnlyReducedFeatures()
    {
        var reduced = Feature(
            RecommendationFeatureKind.Tag,
            "REDUCED",
            "不喜欢的标签");
        var profile = new[]
        {
            new RecommendationFeatureProfile(
                reduced,
                0,
                RecommendationAdjustment.Reduce,
                []),
        };

        var result = RecommendationScorer.Rank(
            profile,
            [new RecommendationCandidate(
                Anime(1, new DateOnly(2026, 1, 1), 9),
                [reduced],
                0)],
            new DateOnly(2026, 8, 1));

        Assert.Empty(result);
    }

    [Fact]
    public void Rank_ManualRefreshPrefersCandidatesOutsidePreviousBatch()
    {
        var feature = Feature(
            RecommendationFeatureKind.Tag,
            "日常",
            "日常");
        var profile = new[]
        {
            new RecommendationFeatureProfile(
                feature,
                5,
                null,
                [],
                IsSavedTag: true),
        };
        var candidates = Enumerable.Range(1, 30)
            .Select(id => new RecommendationCandidate(
                Anime(id, new DateOnly(2026, 1, 1), 9 - id * 0.01),
                [feature],
                0))
            .ToArray();
        var previousIds = Enumerable.Range(1, 20).ToHashSet();

        var result = RecommendationScorer.Rank(
            profile,
            candidates,
            new DateOnly(2026, 8, 1),
            previousIds);

        Assert.Equal(20, result.Count);
        Assert.All(
            Enumerable.Range(21, 10),
            id => Assert.Contains(result, item => item.Anime.ID == id));
    }

    [Fact]
    public void Rank_DoesNotReturnDuplicateAnimeIds()
    {
        var feature = Feature(
            RecommendationFeatureKind.Tag,
            "日常",
            "日常");
        var profile = new[]
        {
            new RecommendationFeatureProfile(
                feature,
                5,
                null,
                []),
        };
        var anime = Anime(1, new DateOnly(2026, 1, 1), 8);

        var result = RecommendationScorer.Rank(
            profile,
            [
                new RecommendationCandidate(anime, [feature], 0),
                new RecommendationCandidate(anime, [feature], 0),
            ],
            new DateOnly(2026, 8, 1));

        Assert.Single(result);
    }

    private static RecommendationFeature Feature(
        RecommendationFeatureKind kind,
        string key,
        string displayName)
        => new(kind, key, displayName);

    private static Anime Anime(int id, DateOnly airDate, double score)
        => new(
            id,
            $"番剧 {id}",
            null,
            [],
            airDate,
            null,
            string.Empty,
            airDate.Year,
            1,
            Score: score);
}
