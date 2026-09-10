using AniMeido.Contracts.Models;

namespace AniMeido.Plugin.Base.Models;

public enum RecommendationFeatureKind
{
    Tag = 0,
    Studio = 1,
    VoiceActor = 2,
}

public enum RecommendationAdjustment
{
    Reduce = -1,
    Like = 1,
}

public sealed record RecommendationFeaturePreference(
    RecommendationFeatureKind Kind,
    string Key,
    string DisplayName,
    RecommendationAdjustment Adjustment,
    DateTimeOffset UpdatedAt);

public sealed record RecommendationHiddenAnime(
    int AnimeId,
    string Title,
    DateTimeOffset HiddenAt)
{
    public string HiddenAtText => $"{HiddenAt.ToLocalTime():yyyy/M/d HH:mm} 隐藏";
}

public sealed record RecommendationFeature(
    RecommendationFeatureKind Kind,
    string Key,
    string DisplayName)
{
    public string KindText => Kind switch
    {
        RecommendationFeatureKind.Tag => "标签",
        RecommendationFeatureKind.Studio => "制作方",
        _ => "声优",
    };
}

public sealed record RecommendationEvidence(
    int AnimeId,
    string Title,
    double Contribution);

public sealed record RecommendationFeatureProfile(
    RecommendationFeature Feature,
    double InferredScore,
    RecommendationAdjustment? Adjustment,
    IReadOnlyList<RecommendationEvidence> Evidence,
    bool IsSavedTag = false)
{
    public double EffectiveScore => InferredScore + (Adjustment switch
    {
        RecommendationAdjustment.Like => 6,
        RecommendationAdjustment.Reduce => -6,
        _ => 0,
    });

    public string DirectionText => Adjustment switch
    {
        RecommendationAdjustment.Like => "已设为喜欢",
        RecommendationAdjustment.Reduce => "已减少推荐",
        _ when IsSavedTag && EffectiveScore > 0 => "来自收藏 Tag",
        _ when EffectiveScore > 0.25 => "推断为喜欢",
        _ when EffectiveScore < -0.25 => "推断为减少",
        _ => "中立",
    };

    public string EvidenceText => IsSavedTag && Evidence.Count == 0
        ? "来自收藏的 Bangumi Tag"
        : Evidence.Count == 0
        ? "来自手工偏好"
        : $"来自 {Evidence.Count} 部番剧";
}

public sealed record RecommendationReason(
    RecommendationFeature Feature,
    double Contribution,
    bool IsReduction,
    string Text);

public sealed record RecommendationItem(
    Anime Anime,
    double Score,
    IReadOnlyList<RecommendationReason> Reasons,
    bool IsPersonalized,
    bool IsRecent)
{
    public string ReasonSummary => string.Join(
        "；",
        Reasons.Select(reason => reason.Text));

    public string Metadata => Anime.Score is > 0
        ? $"Bangumi {Anime.Score:F1} · {(IsRecent ? "近三年" : "经典作品")}"
        : IsRecent ? "近三年" : "经典作品";
}

public sealed record RecommendationSnapshot(
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    bool IsPersonalized,
    IReadOnlyList<RecommendationItem> Items)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record RecommendationGeneration(
    RecommendationSnapshot Snapshot,
    IReadOnlyList<RecommendationFeatureProfile> Profile);

internal sealed record RecommendationSeed(
    int AnimeId,
    string Title,
    double Weight);

internal sealed record RecommendationCandidate(
    Anime Anime,
    IReadOnlyList<RecommendationFeature> Features,
    double SourceScore);
