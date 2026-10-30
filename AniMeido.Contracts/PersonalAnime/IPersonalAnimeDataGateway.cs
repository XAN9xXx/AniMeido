using AniMeido.Contracts.Models;

namespace AniMeido.Contracts.PersonalAnime;

/// <summary>个人资料快照请求的类别组合；类别限制返回字段，不授予新的写入权限。</summary>
[Flags]
public enum PersonalAnimeDataCategory
{
    None = 0,
    PublicMetadata = 1 << 0,
    Tracking = 1 << 1,
    PlansAndProgress = 1 << 2,
    PersonalRating = 1 << 3,
    ArchiveTextAndHistory = 1 << 4,
    /// <summary>已保存的资料标签；历史协议名保留，不限定资料来自 Bangumi。</summary>
    SavedBangumiTags = 1 << 5,
    BrowseSummary = 1 << 6,
    RecommendationProfile = 1 << 7,
}

/// <summary>在个人记录中选择作品的筛选条件；Limit 是请求数量，实际范围由宿主限制。</summary>
public sealed record PersonalAnimeSelectionQuery(
    string? SearchText = null,
    IReadOnlyList<AnimeTrackingStatus>? TrackingStatuses = null,
    bool PlansOnly = false,
    bool ArchivesOnly = false,
    int Limit = 100);

/// <summary>供用户选择的作品摘要，AnimeId 可为公共身份或本地保留身份。</summary>
public sealed record PersonalAnimeSelectionItem(
    int AnimeId,
    string Title,
    AnimeTrackingStatus? TrackingStatus,
    bool HasPlan,
    bool HasArchive,
    double? PersonalRating,
    DateTimeOffset UpdatedAt);

/// <summary>本次快照的使用目的、作品范围与返回类别，不是长期授权凭证。</summary>
public sealed record PersonalAnimeContextRequest(
    string Purpose,
    IReadOnlyList<int> AnimeIds,
    PersonalAnimeDataCategory Categories);

/// <summary>按本次请求组装的资料快照；Categories 说明请求范围，不保证每项资料均可取得。</summary>
public sealed record PersonalAnimeContextSnapshot(
    string SnapshotId,
    string Purpose,
    PersonalAnimeDataCategory Categories,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PersonalAnimeContextItem> Items,
    IReadOnlyList<string> SavedBangumiTags,
    IReadOnlyList<PersonalAnimePreferenceFeature> PreferenceProfile);

/// <summary>单部作品的资料和个人记录；未请求或不可用的内容以相应空值、空列表表示。</summary>
/// <param name="BangumiScore">当前资料评分；保留历史字段名以兼容已有协议。</param>
/// <param name="BangumiTags">当前资料标签；保留历史字段名以兼容已有协议。</param>
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

/// <summary>作品计划的优先级、目标日期和列表顺序。</summary>
public sealed record PersonalAnimePlan(
    int Priority,
    DateOnly? TargetStartDate,
    int SortOrder,
    DateTimeOffset UpdatedAt);

/// <summary>已记录的播放集数和秒级进度；LastWatchedAt 是记录的观看时间。</summary>
public sealed record PersonalAnimeProgress(
    int CurrentEpisode,
    double PositionSeconds,
    double DurationSeconds,
    DateTimeOffset LastWatchedAt);

/// <summary>一条档案感想及其发生时间；EpisodeNumber 为空时不限定集数。</summary>
public sealed record PersonalAnimeArchiveEntry(
    string EntryId,
    DateTimeOffset OccurredAt,
    int? EpisodeNumber,
    string Body);

/// <summary>观看事件的集数区间和记录内容；IsManual 区分手工记录与播放记录。</summary>
public sealed record PersonalAnimeWatchEvent(
    string EventId,
    DateTimeOffset OccurredAt,
    int EpisodeFrom,
    int EpisodeTo,
    int? EstimatedMinutes,
    string Note,
    bool IsManual);

/// <summary>作品的累计浏览次数与最近浏览时间，不包含完整浏览事件。</summary>
public sealed record PersonalAnimeBrowseSummary(
    int ViewCount,
    DateTimeOffset LastViewedAt);

/// <summary>推荐画像中的一项特征及证据；ManualAdjustment 为空表示没有手动设置。</summary>
public sealed record PersonalAnimePreferenceFeature(
    string Kind,
    string Key,
    string DisplayName,
    double InferredScore,
    int? ManualAdjustment,
    bool IsSavedTag,
    IReadOnlyList<string> EvidenceTitles);

/// <summary>当前写回能力支持的业务动作；数值属于既有跨进程协议。</summary>
public enum PersonalAnimeChangeKind
{
    SetTrackingStatus = 0,
    UpsertPlan = 1,
    ReplaceArchiveSummary = 2,
    AppendArchiveEntry = 3,
}

/// <summary>用户已确认的一项业务变更及幂等标识，不直接表示已经提交。</summary>
/// <param name="ChangeId">跨来源全局唯一的变更 ID；同一 ID 的重试须保持变更正文不变。</param>
/// <param name="AnimeId">当前写入能力只接受正公共 ID；本地保留作品的写入支持另行规划。</param>
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

/// <summary>同一来源提交的一批变更，各项仍独立提交。</summary>
/// <param name="SourceId">来源归属及能力限制标识，不是 ChangeId 的去重命名空间。</param>
/// <param name="Changes">逐项独立提交，不是整批原子事务；已提交项不会随后续失败撤销。</param>
public sealed record PersonalAnimeChangeSet(
    string SourceId,
    IReadOnlyList<PersonalAnimeChange> Changes);

/// <summary>单项写回结果；WasAlreadyApplied 表示命中既有回执，不是再次执行。</summary>
public sealed record PersonalAnimeChangeResult(
    string ChangeId,
    bool Applied,
    bool WasAlreadyApplied,
    string Message);

/// <summary>本批逐项写回结果，允许同时包含成功与失败。</summary>
public sealed record PersonalAnimeChangeApplyResult(
    IReadOnlyList<PersonalAnimeChangeResult> Results);

/// <summary>
/// 向进程外能力提供用户明确授权的个人资料，只应用已确认的业务变更。
/// </summary>
public interface IPersonalAnimeDataGateway
{
    /// <summary>可列出正公共 ID 与负本地保留 ID；可选择不代表当前支持建立该作品的快照或写回。</summary>
    Task<IReadOnlyList<PersonalAnimeSelectionItem>> QuerySelectionAsync(
        PersonalAnimeSelectionQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>当前仅为正公共 ID 建立作品快照；仅偏好/标签任务可不选作品。</summary>
    Task<PersonalAnimeContextSnapshot> BuildContextAsync(
        PersonalAnimeContextRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 每项变更与其去重回执一同提交。提交后的提醒处理失败不撤销已提交结果。
    /// 取消或连接中断不保证整批均未写入；重试沿用原 ChangeId 与正文，不能重新生成 ID。
    /// </summary>
    Task<PersonalAnimeChangeApplyResult> ApplyChangesAsync(
        PersonalAnimeChangeSet changeSet,
        CancellationToken cancellationToken = default);
}
