using AniMeido.Contracts.Models;

namespace AniMeido.Plugin.Base.Models;

public enum AnimePlanPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3,
}

public enum PlanReminderKind
{
    RelativeToTargetDate = 0,
    Absolute = 1,
}

public enum PlanReminderState
{
    Pending = 0,
    Handled = 1,
    Cancelled = 2,
}

public sealed record AnimePlan(
    int AnimeId,
    string TitleSnapshot,
    AnimePlanPriority Priority,
    DateOnly? TargetStartDate,
    int SortOrder,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? ArchivedAt);

public sealed record PlanReminder(
    string ReminderId,
    int AnimeId,
    PlanReminderKind Kind,
    int? RelativeDays,
    TimeOnly? TimeOfDay,
    DateTimeOffset? AbsoluteAt,
    DateTimeOffset ScheduledFor,
    PlanReminderState State,
    DateTimeOffset? CatchUpSentAt,
    DateTimeOffset? HandledAt);

public sealed record AnimeProgressSnapshot(
    int AnimeId,
    int CurrentEpisode,
    double PositionSeconds,
    double DurationSeconds,
    DateTimeOffset LastWatchedAt);

public sealed record EpisodeProgress(
    int AnimeId,
    int EpisodeNumber,
    double PositionSeconds,
    double DurationSeconds,
    bool IsCompleted,
    DateTimeOffset LastWatchedAt);

public sealed record WatchSession(
    string EventId,
    int AnimeId,
    int EpisodeNumber,
    double PositionSeconds,
    double DurationSeconds,
    bool IsCompleted,
    DateTimeOffset ObservedAt);

public sealed record ActionCenterItem(
    Anime Anime,
    AnimeTrackingStatus? TrackingStatus,
    AnimePlan? Plan,
    AnimeProgressSnapshot? Progress,
    bool IsOverdue);
