using AniMeido.Plugin.Base.Models;

namespace AniMeido.Plugin.Base.Services;

internal static class RecommendationScorer
{
    private const int RecentTarget = 14;
    private const int ClassicTarget = 6;
    private const double SavedTagWeight = 2.5;

    public static IReadOnlyList<RecommendationFeatureProfile> BuildProfile(
        IReadOnlyList<RecommendationSeed> seeds,
        IReadOnlyDictionary<int, IReadOnlyList<RecommendationFeature>>
            featuresByAnime,
        IReadOnlyList<RecommendationFeaturePreference> preferences,
        IReadOnlyList<string>? savedTags = null)
    {
        var scores = new Dictionary<
            (RecommendationFeatureKind Kind, string Key),
            FeatureAccumulator>();
        foreach (var seed in seeds)
        {
            if (!featuresByAnime.TryGetValue(seed.AnimeId, out var features))
            {
                continue;
            }

            foreach (var group in features.GroupBy(feature => feature.Kind))
            {
                var normalized = seed.Weight / Math.Sqrt(group.Count());
                foreach (var feature in group)
                {
                    var key = (feature.Kind, feature.Key);
                    if (!scores.TryGetValue(key, out var accumulator))
                    {
                        accumulator = new FeatureAccumulator(feature);
                        scores.Add(key, accumulator);
                    }

                    accumulator.Score += normalized;
                    accumulator.Evidence.Add(new RecommendationEvidence(
                        seed.AnimeId,
                        seed.Title,
                        normalized));
                }
            }
        }

        foreach (var savedTag in savedTags ?? [])
        {
            if (string.IsNullOrWhiteSpace(savedTag))
            {
                continue;
            }

            var normalized = RecommendationCandidateProvider.NormalizeTag(
                savedTag);
            var key = (RecommendationFeatureKind.Tag, normalized);
            if (!scores.TryGetValue(key, out var accumulator))
            {
                accumulator = new FeatureAccumulator(
                    new RecommendationFeature(
                        RecommendationFeatureKind.Tag,
                        normalized,
                        savedTag.Trim()));
                scores.Add(key, accumulator);
            }

            accumulator.Score += SavedTagWeight;
            accumulator.IsSavedTag = true;
        }

        var preferenceMap = preferences.ToDictionary(
            item => (item.Kind, item.Key),
            item => item,
            FeatureKeyComparer.Instance);
        foreach (var preference in preferences)
        {
            var key = (preference.Kind, preference.Key);
            if (!scores.ContainsKey(key))
            {
                scores.Add(
                    key,
                    new FeatureAccumulator(new RecommendationFeature(
                        preference.Kind,
                        preference.Key,
                        preference.DisplayName)));
            }
        }

        return scores.Values
            .Select(accumulator =>
            {
                preferenceMap.TryGetValue(
                    (accumulator.Feature.Kind, accumulator.Feature.Key),
                    out var preference);
                return new RecommendationFeatureProfile(
                    accumulator.Feature,
                    accumulator.Score,
                    preference?.Adjustment,
                    accumulator.Evidence
                        .OrderByDescending(item => Math.Abs(item.Contribution))
                        .Take(3)
                        .ToArray(),
                    accumulator.IsSavedTag);
            })
            .OrderByDescending(item => Math.Abs(item.EffectiveScore))
            .ThenBy(item => item.Feature.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    public static IReadOnlyList<RecommendationItem> Rank(
        IReadOnlyList<RecommendationFeatureProfile> profile,
        IReadOnlyList<RecommendationCandidate> candidates,
        DateOnly today,
        IReadOnlySet<int>? previouslyRecommendedIds = null)
    {
        var profileMap = profile.ToDictionary(
            item => (item.Feature.Kind, item.Feature.Key),
            item => item,
            FeatureKeyComparer.Instance);
        var recentBoundary = today.AddYears(-3);
        var scored = new List<ScoredCandidate>();
        foreach (var candidate in candidates)
        {
            var contributions = candidate.Features
                .DistinctBy(feature => (feature.Kind, feature.Key))
                .Select(feature =>
                {
                    profileMap.TryGetValue(
                        (feature.Kind, feature.Key),
                        out var match);
                    return (Feature: feature, Profile: match);
                })
                .Where(item => item.Profile is not null)
                .Select(item => new FeatureContribution(
                    item.Feature,
                    item.Profile!,
                    item.Profile!.EffectiveScore))
                .ToArray();
            var positives = contributions
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ToArray();
            if (positives.Length == 0)
            {
                continue;
            }

            var reductions = contributions
                .Where(item => item.Score < 0)
                .OrderBy(item => item.Score)
                .ToArray();
            var selectedPositives = positives
                .OrderByDescending(item => GetExplicitPreferencePriority(
                    item.Profile))
                .ThenByDescending(item => item.Score)
                .Take(3)
                .ToArray();
            var featureScore = selectedPositives.Sum(item => item.Score)
                + reductions.Take(2).Sum(item => item.Score);
            var publicPrior = candidate.Anime.Score is null
                ? 0
                : (candidate.Anime.Score.Value - 5) * 0.25;
            var reasons = selectedPositives
                .Take(2)
                .Select(item => CreateReason(item, isReduction: false))
                .ToList();
            if (reductions.FirstOrDefault() is { } reduction)
            {
                reasons.Add(CreateReason(reduction, isReduction: true));
            }

            var isRecent = candidate.Anime.AirDate is { } airDate
                && airDate >= recentBoundary;
            scored.Add(new ScoredCandidate(
                new RecommendationItem(
                    candidate.Anime,
                    featureScore + publicPrior + candidate.SourceScore,
                    reasons,
                    true,
                    isRecent),
                positives[0].Feature.Key));
        }

        var recent = scored.Where(item => item.Item.IsRecent)
            .OrderBy(item => previouslyRecommendedIds?.Contains(
                item.Item.Anime.ID) == true)
            .ThenByDescending(item => item.Item.Score)
            .ToList();
        var classic = scored.Where(item => !item.Item.IsRecent)
            .OrderBy(item => previouslyRecommendedIds?.Contains(
                item.Item.Anime.ID) == true)
            .ThenByDescending(item => item.Item.Score)
            .ToList();
        var selected = TakeDiverse(recent, RecentTarget);
        selected.AddRange(TakeDiverse(classic, ClassicTarget));
        if (selected.Count < RecentTarget + ClassicTarget)
        {
            var selectedIds = selected.Select(item => item.Anime.ID).ToHashSet();
            selected.AddRange(
                TakeDiverse(
                    recent.Concat(classic)
                        .Where(item => !selectedIds.Contains(item.Item.Anime.ID))
                        .ToList(),
                    RecentTarget + ClassicTarget - selected.Count));
        }

        return selected;
    }

    private static List<RecommendationItem> TakeDiverse(
        List<ScoredCandidate> source,
        int count)
    {
        var result = new List<RecommendationItem>(count);
        string? lastKey = null;
        var sameKeyCount = 0;
        while (result.Count < count && source.Count > 0)
        {
            var index = source.FindIndex(item =>
                !string.Equals(item.PrimaryFeatureKey, lastKey, StringComparison.Ordinal)
                || sameKeyCount < 3);
            if (index < 0)
            {
                index = 0;
            }

            var selected = source[index];
            source.RemoveAt(index);
            result.Add(selected.Item);
            if (string.Equals(
                selected.PrimaryFeatureKey,
                lastKey,
                StringComparison.Ordinal))
            {
                sameKeyCount++;
            }
            else
            {
                lastKey = selected.PrimaryFeatureKey;
                sameKeyCount = 1;
            }
        }

        return result;
    }

    private static RecommendationReason CreateReason(
        FeatureContribution contribution,
        bool isReduction)
    {
        var featureLabel = contribution.Feature.Kind switch
        {
            RecommendationFeatureKind.Tag => "Tag",
            RecommendationFeatureKind.Studio => "制作方",
            RecommendationFeatureKind.VoiceActor => "声优",
            _ => "特征",
        };
        string text;
        if (isReduction)
        {
            text = $"已降低你对{featureLabel}“{contribution.Feature.DisplayName}”的偏好";
        }
        else if (contribution.Profile.Adjustment
            == RecommendationAdjustment.Like)
        {
            text = $"你已将{featureLabel}“{contribution.Feature.DisplayName}”设为喜欢";
        }
        else if (contribution.Profile.IsSavedTag)
        {
            text = $"因为你收藏了 Tag“{contribution.Feature.DisplayName}”";
        }
        else if (contribution.Profile.Evidence.FirstOrDefault() is { } evidence)
        {
            text = $"因为你喜欢《{evidence.Title}》，且同样包含{featureLabel}“{contribution.Feature.DisplayName}”";
        }
        else
        {
            text = $"符合你对{featureLabel}“{contribution.Feature.DisplayName}”的偏好";
        }

        return new RecommendationReason(
            contribution.Feature,
            contribution.Score,
            isReduction,
            text);
    }

    private static int GetExplicitPreferencePriority(
        RecommendationFeatureProfile profile)
        => profile.Adjustment == RecommendationAdjustment.Like
            ? 2
            : profile.IsSavedTag ? 1 : 0;

    private sealed class FeatureAccumulator(RecommendationFeature feature)
    {
        public RecommendationFeature Feature { get; } = feature;

        public double Score { get; set; }

        public List<RecommendationEvidence> Evidence { get; } = [];

        public bool IsSavedTag { get; set; }
    }

    private sealed record FeatureContribution(
        RecommendationFeature Feature,
        RecommendationFeatureProfile Profile,
        double Score);

    private sealed record ScoredCandidate(
        RecommendationItem Item,
        string PrimaryFeatureKey);

    private sealed class FeatureKeyComparer :
        IEqualityComparer<(RecommendationFeatureKind Kind, string Key)>
    {
        public static FeatureKeyComparer Instance { get; } = new();

        public bool Equals(
            (RecommendationFeatureKind Kind, string Key) x,
            (RecommendationFeatureKind Kind, string Key) y)
            => x.Kind == y.Kind
                && string.Equals(x.Key, y.Key, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(
            (RecommendationFeatureKind Kind, string Key) obj)
            => HashCode.Combine(
                obj.Kind,
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Key));
    }
}
