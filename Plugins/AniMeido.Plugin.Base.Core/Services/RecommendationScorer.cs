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
            FeatureAccumulator>(FeatureKeyComparer.Instance);
        foreach (var seed in seeds)
        {
            if (!featuresByAnime.TryGetValue(seed.AnimeId, out var features))
            {
                continue;
            }

            foreach (var group in features.DistinctBy(feature => (feature.Kind, feature.Key),
                FeatureKeyComparer.Instance).GroupBy(feature => feature.Kind))
            {
                var divisor = Math.Sqrt(group.Count());
                foreach (var feature in group)
                {
                    var key = (feature.Kind, feature.Key);
                    if (!scores.TryGetValue(key, out var accumulator))
                    {
                        accumulator = new FeatureAccumulator(feature);
                        scores.Add(key, accumulator);
                    }

                    foreach (var signal in seed.Signals is { Count: > 0 }
                        ? seed.Signals
                        : [new RecommendationSignal(
                            RecommendationEvidenceSource.Unknown, seed.Weight)])
                    {
                        var normalized = signal.Weight / divisor;
                        accumulator.Score += normalized;
                        accumulator.Evidence.Add(new RecommendationEvidence(
                            seed.AnimeId, seed.Title, normalized, signal.Source));
                    }
                }
            }
        }

        foreach (var savedTag in (savedTags ?? []).Where(tag => !string.IsNullOrWhiteSpace(tag))
            .DistinctBy(RecommendationCandidateProvider.NormalizeTag))
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
                    SelectEvidence(accumulator.Evidence),
                    accumulator.IsSavedTag);
            })
            .OrderByDescending(item => Math.Abs(item.EffectiveScore))
            .ThenBy(item => item.Feature.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<RecommendationEvidence> SelectEvidence(
        IReadOnlyList<RecommendationEvidence> evidence)
    {
        // 一个作品先合并贡献，再选择与净方向一致的行为作为例证。
        var works = evidence.GroupBy(item => item.AnimeId).Select(group =>
        {
            var total = group.Sum(item => item.Contribution);
            var source = group.OrderByDescending(item => Math.Sign(item.Contribution) == Math.Sign(total))
                .ThenByDescending(item => Math.Abs(item.Contribution)).ThenBy(item => item.Source).First();
            return source with { Contribution = total };
        }).Where(item => item.Contribution != 0).ToArray();
        var selected = works.Where(item => item.Contribution > 0)
            .OrderByDescending(item => item.Contribution).Take(1)
            .Concat(works.Where(item => item.Contribution < 0)
                .OrderBy(item => item.Contribution).Take(1))
            .DistinctBy(item => item.AnimeId)
            .ToList();
        selected.AddRange(works
            .Where(item => selected.All(chosen => chosen.AnimeId != item.AnimeId))
            .GroupBy(item => item.AnimeId)
            .Select(group => group.OrderByDescending(item => Math.Abs(item.Contribution)).First())
            .OrderByDescending(item => Math.Abs(item.Contribution))
            .Take(3 - selected.Count));
        return selected.ToArray();
    }

    public static IReadOnlyList<RecommendationItem> Rank(
        IReadOnlyList<RecommendationFeatureProfile> profile,
        IReadOnlyList<RecommendationCandidate> candidates,
        DateOnly today,
        bool requirePositiveMatch = true)
    {
        var profileMap = profile.ToDictionary(
            item => (item.Feature.Kind, item.Feature.Key),
            item => item,
            FeatureKeyComparer.Instance);
        var recentBoundary = today.AddYears(-3);
        var scored = new List<ScoredCandidate>();
        var candidateSet = candidates.DistinctBy(item => item.Anime.ID).ToArray();
        foreach (var candidate in candidateSet)
        {
            var contributions = candidate.Features
                .DistinctBy(feature => (feature.Kind, feature.Key), FeatureKeyComparer.Instance)
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
                .Where(item => item.Score > 0
                    && item.Profile.IsActiveForRecommendation)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Feature.Kind)
                .ThenBy(item => item.Feature.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (requirePositiveMatch && positives.Length == 0)
            {
                continue;
            }

            var reductions = contributions
                .Where(item => item.Score < 0)
                .OrderBy(item => item.Score)
                .ThenBy(item => item.Feature.Kind)
                .ThenBy(item => item.Feature.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var selectedPositives = positives.Take(3).ToArray();
            var selectedReductions = reductions.Where(item => item.Profile.Adjustment == RecommendationAdjustment.Reduce)
                .Concat(reductions.Where(item => item.Profile.Adjustment != RecommendationAdjustment.Reduce).Take(2))
                .ToArray();
            var featureScore = selectedPositives.Sum(item => item.Score)
                + selectedReductions.Sum(item => item.Score);
            // Reward corroborating preferences without counting a fourth match
            // or scaling the same weights a second time.
            var corroborationBonus = Math.Max(0, selectedPositives.Length - 1) * 0.1;
            var publicPrior = candidate.Anime.Score is null
                ? 0
                : (candidate.Anime.Score.Value - 5) * 0.25;
            var reasons = selectedPositives
                .OrderByDescending(item => GetExplicitPreferencePriority(item.Profile))
                .Take(2)
                .Select(item => CreateReason(item, isReduction: false))
                .ToList();
            if (selectedReductions.FirstOrDefault() is { } reduction)
            {
                reasons.Add(CreateReason(reduction, isReduction: true));
            }

            var isRecent = RecommendationCandidate.IsRecentSince(candidate.Anime, recentBoundary, candidate.PartialDateEnd);
            scored.Add(new ScoredCandidate(
                new RecommendationItem(
                    candidate.Anime,
                    featureScore + corroborationBonus + publicPrior,
                    reasons,
                    requirePositiveMatch,
                    isRecent),
                selectedPositives.Length > 0
                    ? $"{selectedPositives[0].Feature.Kind}:{selectedPositives[0].Feature.Key.ToUpperInvariant()}" : "popular",
                candidate.Features.Where(feature => feature.Kind == RecommendationFeatureKind.Studio)
                    .Select(feature => feature.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                reductions.Any(item => item.Profile.Adjustment == RecommendationAdjustment.Reduce)));
        }

        if (!requirePositiveMatch)
            return scored.OrderByDescending(item => item.Item.Score).ThenBy(item => item.Item.Anime.ID)
                .Take(RecentTarget + ClassicTarget).Select(item => item.Item).ToArray();

        // 先用正分候选完成一批；只有不足时才让非正分候选补位。
        var recent = scored.Where(item => item.Item.IsRecent && item.Item.Score > 0)
            .OrderByDescending(item => item.Item.Score)
            .ThenBy(item => item.Item.Anime.ID)
            .ToList();
        var classic = scored.Where(item => !item.Item.IsRecent && item.Item.Score > 0)
            .OrderByDescending(item => item.Item.Score)
            .ThenBy(item => item.Item.Anime.ID)
            .ToList();
        // 三个阶段属于同一批推荐，共用计数，避免切到经典或补足名额时重新堆叠相同依据。
        var counts = new SelectionCounts();
        var selected = TakeDiverse(recent, RecentTarget - 1, counts);
        selected.AddRange(TakeExplore(recent, 1, counts));
        selected.AddRange(TakeDiverse(classic, ClassicTarget - 1, counts));
        selected.AddRange(TakeExplore(classic, 1, counts));
        if (selected.Count < RecentTarget + ClassicTarget)
        {
            var selectedIds = selected.Select(item => item.Anime.ID).ToHashSet();
            selected.AddRange(
                TakeDiverse(
                    recent.Concat(classic)
                        .Where(item => !selectedIds.Contains(item.Item.Anime.ID))
                        .OrderByDescending(item => item.Item.Score).ThenBy(item => item.Item.Anime.ID)
                        .ToList(),
                    RecentTarget + ClassicTarget - selected.Count, counts));
        }

        if (selected.Count < RecentTarget + ClassicTarget)
            selected.AddRange(TakeDiverse(scored.Where(item => item.Item.Score <= 0)
                .OrderByDescending(item => item.Item.Score).ThenBy(item => item.Item.Anime.ID).ToList(),
                RecentTarget + ClassicTarget - selected.Count, counts));

        return selected;
    }

    private static List<RecommendationItem> TakeExplore(
        List<ScoredCandidate> source, int count, SelectionCounts counts)
    {
        // 探索优先从正向候选里换口味；“减少”仍是降权，候选不足时保留可补足的作品。
        var selected = source.OrderBy(item => item.Item.Score <= 0)
            .ThenBy(item => item.HasManualReduction)
            .ThenBy(item => !counts.CanTake(item))
            .ThenByDescending(item => item.Item.Score)
            .ThenBy(item => item.Item.Anime.ID)
            .Take(count).ToArray();
        foreach (var item in selected)
        {
            source.Remove(item);
            counts.Add(item);
        }
        return selected.Select(item => item.Item).ToList();
    }

    private static List<RecommendationItem> TakeDiverse(
        List<ScoredCandidate> source,
        int count,
        SelectionCounts counts)
    {
        var result = new List<RecommendationItem>(count);
        while (result.Count < count && source.Count > 0)
        {
            var index = source.FindIndex(counts.CanTake);
            if (index < 0)
            {
                index = 0;
            }

            var selected = source[index];
            source.RemoveAt(index);
            result.Add(selected.Item);
            counts.Add(selected);
        }

        return result;
    }

    private static RecommendationReason CreateReason(
        FeatureContribution contribution,
        bool isReduction)
    {
        var featureLabel = contribution.Feature.Kind switch
        {
            RecommendationFeatureKind.Tag => "标签",
            RecommendationFeatureKind.Studio => "制作方",
            RecommendationFeatureKind.VoiceActor => "声优",
            _ => "特征",
        };
        var name = contribution.Feature.DisplayName;
        string text;
        if (isReduction)
        {
            text = $"有你不太感兴趣的{featureLabel}“{name}”";
        }
        else if (contribution.Profile.Adjustment
            == RecommendationAdjustment.Like)
        {
            text = $"你把{featureLabel}“{name}”设为了喜欢";
        }
        else if (contribution.Profile.IsSavedTag)
        {
            text = $"你收藏了标签“{name}”";
        }
        else if (contribution.Profile.Evidence
            .Where(item => item.Contribution > 0)
            .OrderByDescending(item => item.Contribution)
            .FirstOrDefault() is { } evidence)
        {
            var source = evidence.Source switch
            {
                RecommendationEvidenceSource.Completed => "看完",
                RecommendationEvidenceSource.Watching => "在追",
                RecommendationEvidenceSource.Following => "关注",
                RecommendationEvidenceSource.PlanToWatch => "计划观看",
                RecommendationEvidenceSource.PersonalRating => "评过分",
                RecommendationEvidenceSource.Browsing => "浏览过",
                RecommendationEvidenceSource.CatchUpPlan => "列入补番计划",
                RecommendationEvidenceSource.EpisodeProgress => "看过几集",
                _ => "记录过",
            };
            var shared = contribution.Feature.Kind switch
            {
                RecommendationFeatureKind.Studio => $"都是 {name} 制作",
                RecommendationFeatureKind.VoiceActor => $"都有 {name} 配音",
                _ => $"都有{featureLabel}“{name}”",
            };
            text = $"和你{source}的《{evidence.Title}》{shared}";
        }
        else
        {
            text = $"你偏好的{featureLabel}“{name}”";
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
        string PrimaryFeatureKey,
        IReadOnlyList<string> StudioKeys,
        bool HasManualReduction);

    private sealed class SelectionCounts
    {
        private readonly Dictionary<string, int> _features = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _studios = new(StringComparer.OrdinalIgnoreCase);

        public bool CanTake(ScoredCandidate item)
            => _features.GetValueOrDefault(item.PrimaryFeatureKey) < 3
                && item.StudioKeys.All(studio => _studios.GetValueOrDefault(studio) < 4);

        public void Add(ScoredCandidate item)
        {
            _features[item.PrimaryFeatureKey] = _features.GetValueOrDefault(item.PrimaryFeatureKey) + 1;
            foreach (var studio in item.StudioKeys) _studios[studio] = _studios.GetValueOrDefault(studio) + 1;
        }
    }

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
