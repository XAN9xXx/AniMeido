namespace AniMeido.Plugin.Base.Models;

public sealed record ArchiveReviewCover(int AnimeId, string Title, DateTimeOffset CreatedAt);

/// <param name="Screenshots">年度回顾预览：按原查询顺序返回至多两张本地文件仍存在的截图。</param>
public sealed record ArchiveReviewData(
    int Year,
    ArchiveStatistics Statistics,
    IReadOnlyList<ArchiveReviewCover> Covers,
    IReadOnlyList<AnnualReviewMoment> Moments,
    IReadOnlyList<AnimeScreenshot> Screenshots);

public sealed record ArchiveScreenshotData(
    IReadOnlyList<AnimeScreenshot> Screenshots,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Tags);

public sealed record ArchiveTimelineData(
    IReadOnlyList<ArchiveEntry> Entries,
    IReadOnlyList<WatchHistoryItem> WatchHistory,
    IReadOnlyList<ArchiveTrackingChange> TrackingChanges);

public sealed record ArchiveDetailData(
    ArchiveTimelineData Timeline,
    IReadOnlyList<AnimeScreenshot> Screenshots);
