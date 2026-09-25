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
                index == 1 ? [liked, reduced] : [liked]))
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
                [savedTag.Feature])],
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
                profile.Select(item => item.Feature).ToArray())],
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
                [reduced])],
            new DateOnly(2026, 8, 1));

        Assert.Empty(result);
    }

    [Fact]
    public void Rank_UsesStableIdOrderForEqualScores()
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
        var candidates = Enumerable.Range(1, 2)
            .Select(id => new RecommendationCandidate(
                Anime(id, new DateOnly(2026, 1, 1), 8),
                [feature]))
            .Reverse().ToArray();

        var result = RecommendationScorer.Rank(
            profile,
            candidates,
            new DateOnly(2026, 8, 1));

        Assert.Equal([1, 2], result.Select(item => item.Anime.ID));
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
                new RecommendationCandidate(anime, [feature]),
                new RecommendationCandidate(anime, [feature]),
            ],
            new DateOnly(2026, 8, 1));

        Assert.Single(result);
    }

    [Fact]
    public void BuildProfile_PreservesBrowsingEvidenceWithoutCallingItALike()
    {
        var feature = Feature(RecommendationFeatureKind.Tag, "TRAVEL", "旅行");
        var profile = RecommendationScorer.BuildProfile(
            [new RecommendationSeed(1, "浏览的作品", 0.5,
                [new RecommendationSignal(RecommendationEvidenceSource.Browsing, 0.5)])],
            new Dictionary<int, IReadOnlyList<RecommendationFeature>>
            {
                [1] = [feature],
            }, []);
        var evidence = Assert.Single(Assert.Single(profile).Evidence);
        Assert.Equal(RecommendationEvidenceSource.Browsing, evidence.Source);
        var result = RecommendationScorer.Rank(profile,
            [new RecommendationCandidate(Anime(2, new DateOnly(2026, 1, 1), 8),
                [feature])], new DateOnly(2026, 8, 1));
        Assert.Contains("浏览过", Assert.Single(result).PrimaryReason);
        Assert.DoesNotContain("喜欢", result[0].PrimaryReason);
    }

    [Fact]
    public void ManualReduction_RemainsNegativeDespiteStrongInference()
    {
        var feature = Feature(RecommendationFeatureKind.Tag, "POPULAR", "常见标签");
        var profile = new RecommendationFeatureProfile(feature, 12,
            RecommendationAdjustment.Reduce, []);
        Assert.True(profile.EffectiveScore < 0);
        var result = RecommendationScorer.Rank([profile],
            [new RecommendationCandidate(Anime(1, new DateOnly(2026, 1, 1), 9),
                [feature])], new DateOnly(2026, 8, 1));
        Assert.Empty(result);
    }

    [Fact]
    public void WeakNeutralInference_DoesNotByItselfRecommendAWork()
    {
        var feature = Feature(RecommendationFeatureKind.Tag, "WEAK", "弱信号");
        var profile = new RecommendationFeatureProfile(feature, 0.2, null, []);
        Assert.False(profile.IsActiveForRecommendation);
        var result = RecommendationScorer.Rank([profile],
            [new RecommendationCandidate(Anime(1, new DateOnly(2026, 1, 1), 9),
                [feature])], new DateOnly(2026, 8, 1));
        Assert.Empty(result);
    }

    [Fact]
    public void Rank_DoesNotChangeScoreWithCandidateWindowComposition()
    {
        var common = Feature(RecommendationFeatureKind.Tag, "COMMON", "常见");
        var distinctive = Feature(RecommendationFeatureKind.Tag, "RARE", "独特");
        var profile = new[]
        {
            new RecommendationFeatureProfile(common, 4, null, []),
            new RecommendationFeatureProfile(distinctive, 3, null, []),
        };
        var candidates = Enumerable.Range(1, 20).Select(id =>
            new RecommendationCandidate(Anime(id, new DateOnly(2026, 1, 1), 8),
                id == 20 ? [distinctive] : [common])).ToArray();

        var today = new DateOnly(2026, 8, 1);
        var alone = RecommendationScorer.Rank(profile, [candidates[19]], today);
        var together = RecommendationScorer.Rank(profile, candidates, today);
        Assert.Equal(Assert.Single(alone).Score,
            Assert.Single(together, item => item.Anime.ID == 20).Score);
        Assert.Equal(1, together[0].Anime.ID);
    }

    [Fact]
    public void Rank_RewardsMultiplePreferencesWithoutCountingAFourth()
    {
        var features = Enumerable.Range(1, 4)
            .Select(id => Feature(RecommendationFeatureKind.Tag, $"TAG-{id}", $"标签{id}"))
            .ToArray();
        var profile = features.Select(feature =>
            new RecommendationFeatureProfile(feature, 2, null, [])).ToArray();
        var candidates = Enumerable.Range(1, 4).Select(id =>
            new RecommendationCandidate(Anime(id, new DateOnly(2026, 1, 1), 8),
                features.Take(id).ToArray())).ToArray();

        var result = RecommendationScorer.Rank(profile, candidates,
            new DateOnly(2026, 8, 1)).ToDictionary(item => item.Anime.ID);
        Assert.Equal(result[1].Score + 2.1, result[2].Score, 6);
        Assert.Equal(result[2].Score + 2.1, result[3].Score, 6);
        Assert.Equal(result[3].Score, result[4].Score, 6);
    }

    [Fact]
    public void Rank_ManualLikeOutranksAWeakerInferredPreference()
    {
        var liked = Feature(RecommendationFeatureKind.Tag, "LIKED", "手动喜欢");
        var inferred = Feature(RecommendationFeatureKind.Tag, "INFERRED", "推断喜欢");
        var profile = new[]
        {
            new RecommendationFeatureProfile(liked, 0,
                RecommendationAdjustment.Like, []),
            new RecommendationFeatureProfile(inferred, 4, null, []),
        };
        var candidates = new[]
        {
            new RecommendationCandidate(Anime(1, new DateOnly(2026, 1, 1), 8),
                [liked]),
            new RecommendationCandidate(Anime(2, new DateOnly(2026, 1, 1), 8),
                [inferred]),
        };

        var result = RecommendationScorer.Rank(profile, candidates,
            new DateOnly(2026, 8, 1));
        Assert.Equal(1, result[0].Anime.ID);
    }

    [Fact]
    public void BuildProfile_UsesDistinctWorksForEvidenceSlots()
    {
        var feature = Feature(RecommendationFeatureKind.Tag, "SAME", "共同标签");
        var seeds = new[]
        {
            new RecommendationSeed(1, "作品一", 3,
                [new RecommendationSignal(RecommendationEvidenceSource.Completed, 2),
                    new RecommendationSignal(RecommendationEvidenceSource.PersonalRating, 1)]),
            new RecommendationSeed(2, "作品二", 1.5,
                [new RecommendationSignal(RecommendationEvidenceSource.Browsing, 1.5)]),
        };
        var profile = RecommendationScorer.BuildProfile(seeds,
            new Dictionary<int, IReadOnlyList<RecommendationFeature>>
            {
                [1] = [feature],
                [2] = [feature],
            }, []);

        Assert.Equal(2, Assert.Single(profile).Evidence.Select(item => item.AnimeId)
            .Distinct().Count());
    }

    [Fact]
    public void EvidenceText_DescribesDisplayedExamplesRatherThanAllSources()
    {
        var feature = Feature(RecommendationFeatureKind.Tag, "SHARED", "共同标签");
        var seeds = Enumerable.Range(1, 6)
            .Select(id => new RecommendationSeed(id, $"作品{id}", 1))
            .ToArray();
        var features = seeds.ToDictionary(seed => seed.AnimeId,
            _ => (IReadOnlyList<RecommendationFeature>)[feature]);

        var profile = RecommendationScorer.BuildProfile(seeds, features, []);

        Assert.Equal(3, Assert.Single(profile).Evidence.Count);
        Assert.Equal("展示 3 部作品的代表性记录", profile[0].EvidenceText);
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
