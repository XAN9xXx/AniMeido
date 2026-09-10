using AniMeido.Contracts.Models;

namespace AniMeido.Contracts.PersonalAnime;

[Flags]
public enum PersonalAnimeDataCategory
{
    None = 0,
    PublicMetadata = 1 << 0,
    Tracking = 1 << 1,
    PlansAndProgress = 1 << 2,
    PersonalRating = 1 << 3,
    ArchiveTextAndHistory = 1 << 4,
    SavedBangumiTags = 1 << 5,
    BrowseSummary = 1 << 6,
    RecommendationProfile = 1 << 7,
}

public sealed record PersonalAnimeSelectionQuery(
    string? SearchText = null,
    IReadOnlyList<AnimeTrackingStatus>? TrackingStatuses = null,
    bool PlansOnly = false,
    bool ArchivesOnly = false,
    int Limit = 100);

public sealed record PersonalAnimeSelectionItem(
    int AnimeId,
    string Title,
    AnimeTrackingStatus? TrackingStatus,
    bool HasPlan,
    bool HasArchive,
    double? PersonalRating,
    DateTimeOffset UpdatedAt);

public sealed record PersonalAnimeContextRequest(
    string Purpose,
    IReadOnlyList<int> AnimeIds,
    PersonalAnimeDataCategory Categories);

public sealed record PersonalAnimeContextSnapshot(
    string SnapshotId,
    string Purpose,
    PersonalAnimeDataCategory Categories,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PersonalAnimeContextItem> Items,
    IReadOnlyList<string> SavedBangumiTags,
    IReadOnlyList<PersonalAnimePreferenceFeature> PreferenceProfile);

public sealed record PersonalAnimeContextItem(
    int AnimeId,
    string Title,
    string? Description,
    DateOnly? AirDate,
    double? BangumiScore,
    IReadOnlyList<string> BangumiTags,
    IReadOnlyList<string> Studios,
    IReadOnlyList<string> VoiceActors,
    AnimeTrackingStatus? TrackingStatus,
    PersonalAnimePlan? Plan,
    PersonalAnimeProgress? Progress,
    double? PersonalRating,
    string? ArchiveSummary,
    IReadOnlyList<PersonalAnimeArchiveEntry> ArchiveEntries,
    IReadOnlyList<PersonalAnimeWatchEvent> WatchHistory,
    PersonalAnimeBrowseSummary? BrowseSummary);

public sealed record PersonalAnimePlan(
    int Priority,
    DateOnly? TargetStartDate,
    int SortOrder,
    DateTimeOffset UpdatedAt);

public sealed record PersonalAnimeProgress(
    int CurrentEpisode,
    double PositionSeconds,
    double DurationSeconds,
    DateTimeOffset LastWatchedAt);

public sealed record PersonalAnimeArchiveEntry(
    string EntryId,
    DateTimeOffset OccurredAt,
    int? EpisodeNumber,
    string Body);

public sealed record PersonalAnimeWatchEvent(
    string EventId,
    DateTimeOffset OccurredAt,
    int EpisodeFrom,
    int EpisodeTo,
    int? EstimatedMinutes,
    string Note,
    bool IsManual);

public sealed record PersonalAnimeBrowseSummary(
    int ViewCount,
    DateTimeOffset LastViewedAt);

public sealed record PersonalAnimePreferenceFeature(
    string Kind,
    string Key,
    string DisplayName,
    double InferredScore,
    int? ManualAdjustment,
    bool IsSavedTag,
    IReadOnlyList<string> EvidenceTitles);

public enum PersonalAnimeChangeKind
{
    SetTrackingStatus = 0,
    UpsertPlan = 1,
    ReplaceArchiveSummary = 2,
    AppendArchiveEntry = 3,
}

public sealed record PersonalAnimeChange(
    string ChangeId,
    PersonalAnimeChangeKind Kind,
    int AnimeId,
    string Title,
    string Reason,
    AnimeTrackingStatus? TrackingStatus = null,
    int? PlanPriority = null,
    DateOnly? PlanTargetStartDate = null,
    string? Text = null,
    int? EpisodeNumber = null,
    DateTimeOffset? OccurredAt = null);

public sealed record PersonalAnimeChangeSet(
    string SourceId,
    IReadOnlyList<PersonalAnimeChange> Changes);

public sealed record PersonalAnimeChangeResult(
    string ChangeId,
    bool Applied,
    bool WasAlreadyApplied,
    string Message);

public sealed record PersonalAnimeChangeApplyResult(
    IReadOnlyList<PersonalAnimeChangeResult> Results);

/// <summary>
/// Provides explicitly scoped personal anime data to an out-of-process
/// capability and applies only user-confirmed domain changes.
/// </summary>
public interface IPersonalAnimeDataGateway
{
    Task<IReadOnlyList<PersonalAnimeSelectionItem>> QuerySelectionAsync(
        PersonalAnimeSelectionQuery query,
        CancellationToken cancellationToken = default);

    Task<PersonalAnimeContextSnapshot> BuildContextAsync(
        PersonalAnimeContextRequest request,
        CancellationToken cancellationToken = default);

    Task<PersonalAnimeChangeApplyResult> ApplyChangesAsync(
        PersonalAnimeChangeSet changeSet,
        CancellationToken cancellationToken = default);
}
