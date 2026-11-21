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

public enum RecommendationEvidenceSource
{
    Unknown,
    Completed,
    Watching,
    Following,
    PlanToWatch,
    Dropped,
    NotInterested,
    PersonalRating,
    Browsing,
    CatchUpPlan,
    EpisodeProgress,
}

internal sealed record RecommendationSignal(
    RecommendationEvidenceSource Source,
    double Weight);

public sealed record RecommendationFeaturePreference(
    RecommendationFeatureKind Kind,
    string Key,
    string DisplayName,
    RecommendationAdjustment Adjustment,
    DateTimeOffset UpdatedAt,
    bool Invalid = false)
{
    public string StateDisplayName => Invalid ? DisplayName + "（已失效）" : DisplayName;
}

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
    double Contribution,
    RecommendationEvidenceSource Source = RecommendationEvidenceSource.Unknown);

public sealed record RecommendationFeatureProfile(
    RecommendationFeature Feature,
    double InferredScore,
    RecommendationAdjustment? Adjustment,
    IReadOnlyList<RecommendationEvidence> Evidence,
    bool IsSavedTag = false)
{
    public double EffectiveScore => Adjustment switch
    {
        RecommendationAdjustment.Like => Math.Max(0, InferredScore) + 6,
        RecommendationAdjustment.Reduce => Math.Min(0, InferredScore) - 6,
        _ => InferredScore,
    };

    public bool IsActiveForRecommendation => EffectiveScore > 0.25;

    public string DirectionText => Adjustment switch
    {
        RecommendationAdjustment.Like => "喜欢 · 手动",
        RecommendationAdjustment.Reduce => "减少 · 手动",
        _ when IsSavedTag && EffectiveScore > 0 => "收藏的标签",
        _ when EffectiveScore > 0.25 => "推测感兴趣",
        _ when EffectiveScore < -0.25 => "减少 · 自动",
        _ => "无偏好",
    };

    public string EvidenceText => IsSavedTag && Evidence.Count == 0
        ? "你收藏了这个标签"
        : Evidence.Count == 0
        ? "你手动设置的"
        : $"展示 {Evidence.Select(item => item.AnimeId).Distinct().Count()} 部参考作品";
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
    public string PrimaryReason => Reasons.FirstOrDefault(reason => !reason.IsReduction)?.Text ?? "换个口味";

    public string ReasonSummary => string.Join(
        "；",
        Reasons.Select(reason => reason.Text));

    private string AgeText => Anime.AirDate is null && Anime.SeasonYear <= 0 ? "年代未知" : IsRecent ? "近三年" : "较早作品";

    public string Metadata => Anime.Score is > 0
        ? $"评分 {Anime.Score:F1} · {AgeText}"
        : AgeText;
}

public sealed record RecommendationSnapshot(
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    bool IsPersonalized,
    IReadOnlyList<RecommendationItem> Items,
    IReadOnlyList<RecommendationFeatureProfile>? RoundProfile = null,
    bool HasMore = false)
{
    public const int CurrentSchemaVersion = 12;
}

public sealed record RecommendationGeneration(
    RecommendationSnapshot Snapshot,
    IReadOnlyList<RecommendationFeatureProfile> Profile);

public sealed record RecommendationPageBatch(
    RecommendationSnapshot Snapshot,
    IReadOnlyList<RecommendationItem> Items);

internal sealed record RecommendationSeed(
    int AnimeId,
    string Title,
    double Weight,
    IReadOnlyList<RecommendationSignal>? Signals = null);

internal sealed record RecommendationCandidate(
    Anime Anime,
    IReadOnlyList<RecommendationFeature> Features,
    DateOnly? PartialDateEnd = null)
{
    internal static bool IsRecentSince(Anime anime, DateOnly boundary, DateOnly? partialDateEnd = null)
    {
        if ((anime.AirDate ?? partialDateEnd) is { } date) return date >= boundary;
        if (anime.SeasonYear is < 1 or > 9999) return false;
        if (anime.SeasonYear != boundary.Year) return anime.SeasonYear > boundary.Year;
        // 只有季度或年份时按已知精度比较，跨过分界的那一季仍可进入近作。
        return anime.SeasonMonth is not (1 or 4 or 7 or 10) || anime.SeasonMonth + 2 >= boundary.Month;
    }
}
