namespace AniMeido.Plugin.Base.Models;

public sealed record AnimeArchive(
    int AnimeId,
    string TitleSnapshot,
    double? PersonalRating,
    string SummaryNote,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ArchiveEntry(
    string EntryId,
    int AnimeId,
    DateTimeOffset OccurredAt,
    int? EpisodeNumber,
    string Body,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public string DisplayTime =>
        OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string EpisodeText => EpisodeNumber is null
        ? string.Empty
        : $"第 {EpisodeNumber} 集";
}

public sealed record AnnualReviewMoment(ArchiveEntry Entry, string AnimeTitle)
{
    public int AnimeId => Entry.AnimeId;

    public string DateText => Entry.OccurredAt.ToLocalTime().ToString("M月d日");

    public string Preview => Entry.Body.Replace('\r', ' ').Replace('\n', ' ').Trim();
}

public sealed record ManualWatchEvent(
    string EventId,
    int AnimeId,
    string TitleSnapshot,
    DateTimeOffset OccurredAt,
    int EpisodeFrom,
    int EpisodeTo,
    int? DurationMinutes,
    string Note,
    DateTimeOffset CreatedAt);

public sealed record WatchHistoryItem(
    string EventId,
    int AnimeId,
    string Title,
    DateTimeOffset OccurredAt,
    int EpisodeFrom,
    int EpisodeTo,
    int? EstimatedMinutes,
    string Note,
    bool IsManual)
{
    public string TimeText =>
        OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string EpisodeText => EpisodeFrom == EpisodeTo
        ? $"第 {EpisodeFrom} 集"
        : $"第 {EpisodeFrom}–{EpisodeTo} 集";

    public string SourceText => IsManual ? "手工补录" : "播放器记录";
}

public sealed record AnimeScreenshot(
    string ScreenshotId,
    string FilePath,
    string Sha256,
    DateTimeOffset CapturedAt,
    string WindowTitle,
    string ProcessName,
    int Width,
    int Height,
    int? AnimeId,
    string? AnimeTitle,
    int? EpisodeNumber,
    double? PlaybackPositionSeconds,
    string ContextNote,
    bool FileExists)
{
    public string ReviewTitle => string.IsNullOrWhiteSpace(AnimeTitle)
        ? "未关联作品" : AnimeTitle;

    public string ReviewCaption => string.IsNullOrWhiteSpace(ContextNote)
        ? CapturedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm")
        : ContextNote;

    public string CaptureClockText => CapturedAt.ToLocalTime().ToString("HH:mm");
}

public sealed record ArchiveListItem(
    AnimeArchive Archive,
    IReadOnlyList<string> Tags,
    int EntryCount,
    int ScreenshotCount,
    AniMeido.Contracts.Models.AnimeTrackingStatus? TrackingStatus)
{
    public string Title => Archive.TitleSnapshot;

    public string Metadata
        => $"{(Archive.PersonalRating is null ? "未评分" : $"{Archive.PersonalRating:0.0} 分")}"
            + $" · {EntryCount} 条感想 · {ScreenshotCount} 张截图";

    public string TagSummary => string.Join("、", Tags);

    public string RatingText => Archive.PersonalRating is { } rating
        ? $"我的评分 {rating:0.0}"
        : "尚未评分";

    public string EntryCountText => $"感想 {EntryCount}";

    public string StatusText => TrackingStatus switch
    {
        AniMeido.Contracts.Models.AnimeTrackingStatus.Watching => "追番中",
        AniMeido.Contracts.Models.AnimeTrackingStatus.PlanToWatch => "补番中",
        AniMeido.Contracts.Models.AnimeTrackingStatus.NotInterested => "不感兴趣",
        AniMeido.Contracts.Models.AnimeTrackingStatus.Following => "关注",
        AniMeido.Contracts.Models.AnimeTrackingStatus.Completed => "已看完",
        AniMeido.Contracts.Models.AnimeTrackingStatus.Dropped => "弃坑",
        AniMeido.Contracts.Models.AnimeTrackingStatus.Blocked => "屏蔽",
        _ => "未标记状态",
    };
}

public sealed record ArchiveTrackingChange(
    DateTimeOffset ChangedAt,
    AniMeido.Contracts.Models.AnimeTrackingStatus NewStatus);

public sealed record ArchiveTimelineItem(
    DateTimeOffset OccurredAt,
    string Kind,
    string Heading,
    string Body,
    ArchiveEntry? Entry = null)
{
    public string DisplayTime => OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

public sealed record ArchiveStatistics(
    DateTimeOffset? RecordingStartedAt,
    int ArchiveCount,
    int RatedCount,
    int EntryCount,
    int ScreenshotCount,
    int TrackingChangeCount,
    int CompletedEpisodeCount,
    int EstimatedWatchMinutes,
    IReadOnlyDictionary<string, int> TagCounts);

public sealed record ScreenshotSettings(
    bool Enabled,
    string RootDirectory,
    bool SoundEnabled,
    bool PopupEnabled)
{
    public static ScreenshotSettings CreateDefault()
    {
        var pictures = Environment.GetFolderPath(
            Environment.SpecialFolder.MyPictures);
        return new ScreenshotSettings(
            true,
            Path.Combine(pictures, "AniMeido", "Screenshots"),
            true,
            true);
    }
}
