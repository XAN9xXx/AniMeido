using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Contracts.Notifications;
using AniMeido.Contracts.PersonalAnime;
using AniMeido.Plugin.Base.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace AniMeido.Plugin.Base.Services;

public sealed class PersonalAnimeDataGateway : IPersonalAnimeDataGateway
{
    private const int MaximumSelectionLimit = PersonalAnimeSelectionReader.MaximumSelectionLimit;
    private const int MaximumContextItems = 50;
    private readonly SqliteConnectionFactory _dbFactory;
    private readonly IAnimeDataSource _dataSource;
    private readonly TrackingService _tracking;
    private readonly ActionCenterService _actionCenter;
    private readonly ArchiveService _archive;
    private readonly SavedTagService _savedTags;
    private readonly BrowseHistoryService _browseHistory;
    private readonly RecommendationService _recommendations;
    private readonly PlanReminderCoordinator _reminders;
    private readonly IAppNotificationService _notifications;
    private readonly ILogger<PersonalAnimeDataGateway> _logger;

    public PersonalAnimeDataGateway(
        SqliteConnectionFactory dbFactory,
        IAnimeDataSource dataSource,
        TrackingService tracking,
        ActionCenterService actionCenter,
        ArchiveService archive,
        SavedTagService savedTags,
        BrowseHistoryService browseHistory,
        RecommendationService recommendations,
        PlanReminderCoordinator reminders,
        IAppNotificationService notifications,
        ILogger<PersonalAnimeDataGateway>? logger = null)
    {
        _dbFactory = dbFactory;
        _dataSource = dataSource;
        _tracking = tracking;
        _actionCenter = actionCenter;
        _archive = archive;
        _savedTags = savedTags;
        _browseHistory = browseHistory;
        _recommendations = recommendations;
        _reminders = reminders;
        _notifications = notifications;
        _logger = logger ?? NullLogger<PersonalAnimeDataGateway>.Instance;
    }

    public Task<IReadOnlyList<PersonalAnimeSelectionItem>> QuerySelectionAsync(
        PersonalAnimeSelectionQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return PersonalAnimeSelectionReader.ReadAsync(_dbFactory, query, cancellationToken);
    }

    public async Task<PersonalAnimeContextSnapshot> BuildContextAsync(
        PersonalAnimeContextRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Purpose);
        var animeIds = request.AnimeIds
            .Where(id => id > 0)
            .Distinct()
            .Take(MaximumContextItems + 1)
            .ToArray();
        var allowsProfileOnly = request.Categories.HasFlag(
                PersonalAnimeDataCategory.RecommendationProfile)
            || request.Categories.HasFlag(
                PersonalAnimeDataCategory.SavedBangumiTags);
        if (animeIds.Length > MaximumContextItems
            || animeIds.Length == 0 && !allowsProfileOnly)
        {
            throw new ArgumentException(
                $"授权快照必须包含 1–{MaximumContextItems} 部番剧，偏好画像任务可不选择番剧。",
                nameof(request));
        }

        var items = await BuildContextItemsAsync(
            animeIds, request.Categories, cancellationToken);

        var savedTags = request.Categories.HasFlag(
            PersonalAnimeDataCategory.SavedBangumiTags)
                ? (await _savedTags.GetSavedTagStatesAsync()).Where(t => !t.Invalid).Select(t => t.Name).ToList()
                : [];
        var preferenceProfile = request.Categories.HasFlag(
            PersonalAnimeDataCategory.RecommendationProfile)
                ? await BuildPreferenceProfileAsync(cancellationToken)
                : [];
        return new PersonalAnimeContextSnapshot(
            Guid.NewGuid().ToString("N"),
            request.Purpose.Trim(),
            request.Categories,
            DateTimeOffset.UtcNow,
            items,
            savedTags,
            preferenceProfile);
    }

    public async Task<PersonalAnimeChangeApplyResult> ApplyChangesAsync(
        PersonalAnimeChangeSet changeSet,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        ArgumentException.ThrowIfNullOrWhiteSpace(changeSet.SourceId);
        if (changeSet.Changes.Count is 0 or > 50)
        {
            throw new ArgumentException(
                "一次确认必须包含 1–50 项变更。",
                nameof(changeSet));
        }

        if (string.Equals(
                changeSet.SourceId,
                "AniMeido.Plugin.AI",
                StringComparison.Ordinal)
            && changeSet.Changes.Any(change =>
                change.TrackingStatus == AnimeTrackingStatus.Completed))
        {
            throw new ArgumentException(
                "AI 插件不得把整部动画标记为已完成。",
                nameof(changeSet));
        }

        var results = new List<PersonalAnimeChangeResult>();
        foreach (var change in changeSet.Changes)
        {
            try
            {
                results.Add(await ApplyChangeAsync(
                    changeSet.SourceId,
                    change,
                    cancellationToken));
            }
            catch (Exception ex) when (
                ex is ArgumentException
                or InvalidOperationException
                or SqliteException)
            {
                if (ex is SqliteException)
                    _logger.LogError(ex, "Personal change {ChangeId} failed", change.ChangeId);

                results.Add(new PersonalAnimeChangeResult(
                    change.ChangeId,
                    false,
                    false,
                    ex.Message));
            }
        }

        return new PersonalAnimeChangeApplyResult(results);
    }

    private async Task<IReadOnlyList<PersonalAnimeContextItem>> BuildContextItemsAsync(
        int[] animeIds,
        PersonalAnimeDataCategory categories,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (animeIds.Length == 0)
        {
            return [];
        }

        var tracking = categories.HasFlag(PersonalAnimeDataCategory.Tracking)
            ? (await _tracking.GetAllTrackingAsync()).ToDictionary(item => item.AnimeId)
            : new Dictionary<int, (int AnimeId, AnimeTrackingStatus Status, string UpdatedAt)>();
        var plans = (await _actionCenter.GetPlansAsync(
            includeArchived: true,
            cancellationToken)).ToDictionary(item => item.AnimeId);
        var progress = categories.HasFlag(PersonalAnimeDataCategory.PlansAndProgress)
            ? await _actionCenter.GetProgressAsync(cancellationToken)
            : new Dictionary<int, AnimeProgressSnapshot>();
        var browse = (await _browseHistory.GetHistoryAsync(
            MaximumSelectionLimit,
            cancellationToken)).ToDictionary(item => item.AnimeId);
        var items = new List<PersonalAnimeContextItem>(animeIds.Length);
        foreach (var animeId in animeIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(await BuildItemAsync(
                animeId,
                categories,
                tracking,
                plans,
                progress,
                browse,
                cancellationToken));
        }

        return items;
    }

    private async Task<PersonalAnimeContextItem> BuildItemAsync(
        int animeId,
        PersonalAnimeDataCategory categories,
        IReadOnlyDictionary<int, (int AnimeId, AnimeTrackingStatus Status, string UpdatedAt)> tracking,
        IReadOnlyDictionary<int, AnimePlan> plans,
        IReadOnlyDictionary<int, AnimeProgressSnapshot> progress,
        IReadOnlyDictionary<int, (int AnimeId, string? Title, DateTime LastViewed, int ViewCount)> browse,
        CancellationToken cancellationToken)
    {
        var needsPublic = categories.HasFlag(
            PersonalAnimeDataCategory.PublicMetadata);
        var detail = needsPublic
            ? await _dataSource.GetAnimeDetailAsync(animeId, cancellationToken)
            : null;
        var archive = categories.HasFlag(
            PersonalAnimeDataCategory.PersonalRating)
            || categories.HasFlag(
                PersonalAnimeDataCategory.ArchiveTextAndHistory)
                ? await _archive.GetArchiveAsync(animeId, cancellationToken)
                : null;
        plans.TryGetValue(animeId, out var plan);
        progress.TryGetValue(animeId, out var progressItem);
        tracking.TryGetValue(animeId, out var trackingItem);
        browse.TryGetValue(animeId, out var browseItem);
        var includeArchive = categories.HasFlag(
            PersonalAnimeDataCategory.ArchiveTextAndHistory);
        var tags = needsPublic
            ? (await _dataSource.GetTagsAsync(animeId, cancellationToken))
                .Select(item => item.Name)
                .Take(30)
                .ToArray()
            : [];
        var studios = needsPublic
            ? (await _dataSource.GetStudioAsync(animeId, cancellationToken))
                .Select(item => item.Name)
                .Take(10)
                .ToArray()
            : [];
        var actors = needsPublic
            ? (await _dataSource.GetCVsAsync(animeId, cancellationToken))
                .Select(item => item.Name)
                .Take(20)
                .ToArray()
            : [];
        var entries = includeArchive
            ? (await _archive.GetEntriesAsync(animeId, cancellationToken))
                .Take(10)
                .Select(item => new PersonalAnimeArchiveEntry(
                    item.EntryId,
                    item.OccurredAt,
                    item.EpisodeNumber,
                    Truncate(item.Body, 4_000) ?? string.Empty))
                .ToArray()
            : [];
        var history = includeArchive
            ? (await _archive.GetWatchHistoryAsync(
                animeId,
                cancellationToken))
                .Take(50)
                .Select(item => new PersonalAnimeWatchEvent(
                    item.EventId,
                    item.OccurredAt,
                    item.EpisodeFrom,
                    item.EpisodeTo,
                    item.EstimatedMinutes,
                    item.Note,
                    item.IsManual))
                .ToArray()
            : [];
        return new PersonalAnimeContextItem(
            animeId,
            detail?.Title
                ?? archive?.TitleSnapshot
                ?? plan?.TitleSnapshot
                ?? browseItem.Title
                ?? AnimeTitleFallback.Untitled,
            needsPublic ? Truncate(detail?.Description, 8_000) : null,
            needsPublic ? detail?.AirDate : null,
            needsPublic ? detail?.Score : null,
            tags,
            studios,
            actors,
            categories.HasFlag(PersonalAnimeDataCategory.Tracking)
                && trackingItem != default
                    ? trackingItem.Status
                    : null,
            categories.HasFlag(PersonalAnimeDataCategory.PlansAndProgress)
                && plan is not null
                    ? new PersonalAnimePlan(
                        (int)plan.Priority,
                        plan.TargetStartDate,
                        plan.SortOrder,
                        plan.UpdatedAt)
                    : null,
            categories.HasFlag(PersonalAnimeDataCategory.PlansAndProgress)
                && progressItem is not null
                    ? new PersonalAnimeProgress(
                        progressItem.CurrentEpisode,
                        progressItem.PositionSeconds,
                        progressItem.DurationSeconds,
                        progressItem.LastWatchedAt)
                    : null,
            categories.HasFlag(PersonalAnimeDataCategory.PersonalRating)
                ? archive?.PersonalRating
                : null,
            includeArchive ? Truncate(archive?.SummaryNote, 8_000) : null,
            entries,
            history,
            categories.HasFlag(PersonalAnimeDataCategory.BrowseSummary)
                && browseItem != default
                    ? new PersonalAnimeBrowseSummary(
                        browseItem.ViewCount,
                        new DateTimeOffset(browseItem.LastViewed))
                    : null);
    }

    private async Task<IReadOnlyList<PersonalAnimePreferenceFeature>>
        BuildPreferenceProfileAsync(CancellationToken cancellationToken)
    {
        var manual = await _recommendations.GetFeaturePreferencesAsync(
            cancellationToken);
        manual = manual.Where(item => !item.Invalid).ToList();
        var profile = _recommendations.LastProfile;
        return profile
            .Select(item => new PersonalAnimePreferenceFeature(
                item.Feature.KindText,
                item.Feature.Key,
                item.Feature.DisplayName,
                item.InferredScore,
                item.Adjustment is null ? null : (int)item.Adjustment.Value,
                item.IsSavedTag,
                item.Evidence.Select(evidence => evidence.Title).ToArray()))
            .Concat(manual
                .Where(item => profile.All(profileItem =>
                    profileItem.Feature.Kind != item.Kind
                    || !string.Equals(
                        profileItem.Feature.Key,
                        item.Key,
                        StringComparison.OrdinalIgnoreCase)))
                .Select(item => new PersonalAnimePreferenceFeature(
                    item.Kind.ToString(),
                    item.Key,
                    item.DisplayName,
                    0,
                    (int)item.Adjustment,
                    false,
                    [])))
            .OrderByDescending(item =>
                Math.Abs(item.InferredScore + (item.ManualAdjustment ?? 0) * 6))
            .Take(50)
            .ToArray();
    }

    private async Task<PersonalAnimeChangeResult> ApplyChangeAsync(
        string sourceId,
        PersonalAnimeChange change,
        CancellationToken cancellationToken)
    {
        using var operation = _dbFactory.Access.BeginWrite();
        ValidateChange(change);
        var payload = JsonSerializer.Serialize(change);
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        await using var connection = await _dbFactory.OpenAsync(
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            cancellationToken);
        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = (SqliteTransaction)transaction;
            existing.CommandText = """
                SELECT PayloadHash FROM external_change_receipts
                WHERE ChangeId = @id
                """;
            existing.Parameters.AddWithValue("@id", change.ChangeId);
            var existingHash = await existing.ExecuteScalarAsync(
                cancellationToken) as string;
            if (existingHash is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                if (!string.Equals(existingHash, hash, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "相同变更 ID 对应了不同内容，已拒绝执行。");
                }

                return new PersonalAnimeChangeResult(
                    change.ChangeId,
                    true,
                    true,
                    "该变更已经应用。");
            }
        }

        await ApplyDomainChangeAsync(
            connection,
            (SqliteTransaction)transaction,
            change,
            cancellationToken);
        await using (var receipt = connection.CreateCommand())
        {
            receipt.Transaction = (SqliteTransaction)transaction;
            receipt.CommandText = """
                INSERT INTO external_change_receipts(
                    ChangeId, SourceId, PayloadHash, Result, AppliedAt)
                VALUES(@id, @source, @hash, 'Applied', @now)
                """;
            receipt.Parameters.AddWithValue("@id", change.ChangeId);
            receipt.Parameters.AddWithValue("@source", sourceId.Trim());
            receipt.Parameters.AddWithValue("@hash", hash);
            receipt.Parameters.AddWithValue(
                "@now",
                DateTimeOffset.UtcNow.ToString("O"));
            await receipt.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        var message = await CompleteCommittedChangeAsync(change);

        return new PersonalAnimeChangeResult(
            change.ChangeId,
            true,
            false,
            message);
    }

    private async Task<string> CompleteCommittedChangeAsync(PersonalAnimeChange change)
    {
        try
        {
            // 主体与回执已经提交；取消只影响提交前，不能丢失成功结果。
            if (change.Kind == PersonalAnimeChangeKind.UpsertPlan)
            {
                var plan = await _actionCenter.GetPlanAsync(change.AnimeId, CancellationToken.None);
                if (plan is not null)
                    await _reminders.RescheduleAnimeAsync(plan, CancellationToken.None);
            }
            else if (change.Kind == PersonalAnimeChangeKind.SetTrackingStatus
                && change.TrackingStatus != AnimeTrackingStatus.PlanToWatch)
            {
                await _notifications.CancelGroupAsync(
                    PlanReminderCoordinator.GetNotificationGroup(change.AnimeId), CancellationToken.None);
            }
            return "变更已应用。";
        }
#pragma warning disable CA1031 // 数据与回执已生效；提醒后处理失败只降级提示，保留原始异常供诊断。
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Personal change {ChangeId} committed; notification follow-up failed", change.ChangeId);
            return "修改已保存，但提醒暂未更新。请稍后在计划中检查提醒。";
        }
#pragma warning restore CA1031
    }

    private static async Task ApplyDomainChangeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PersonalAnimeChange change,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        switch (change.Kind)
        {
            case PersonalAnimeChangeKind.SetTrackingStatus:
                await TrackingService.SetStatusInTransactionAsync(
                    connection,
                    transaction,
                    change.AnimeId,
                    change.TrackingStatus!.Value,
                    now,
                    recordEvent: true,
                    recordOnlyOnChange: true,
                    eventId: change.ChangeId,
                    cancellationToken: cancellationToken);
                break;
            case PersonalAnimeChangeKind.UpsertPlan:
                await ActionCenterService.UpsertPlanInTransactionAsync(
                    connection,
                    transaction,
                    change.AnimeId,
                    change.Title,
                    (AnimePlanPriority)change.PlanPriority!.Value,
                    change.PlanTargetStartDate,
                    sortOrder: 0,
                    preserveSortOrder: true,
                    now: now,
                    cancellationToken: cancellationToken);
                break;
            case PersonalAnimeChangeKind.ReplaceArchiveSummary:
                await ArchiveService.UpsertArchiveInTransactionAsync(
                    connection,
                    transaction,
                    change.AnimeId,
                    change.Title,
                    rating: null,
                    summary: change.Text!,
                    now: now,
                    preserveRating: true,
                    preserveSummary: false,
                    cancellationToken: cancellationToken);
                break;
            case PersonalAnimeChangeKind.AppendArchiveEntry:
                await ArchiveService
                    .EnsureArchiveAndAddEntryInTransactionAsync(
                        connection,
                        transaction,
                        change.ChangeId,
                        change.AnimeId,
                        change.Title,
                        (change.OccurredAt ?? DateTimeOffset.UtcNow)
                            .ToUniversalTime(),
                        change.EpisodeNumber,
                        change.Text!,
                        now,
                        cancellationToken: cancellationToken);
                break;
            default:
                throw new InvalidOperationException("不支持的变更类型。");
        }
    }

    private static void ValidateChange(PersonalAnimeChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentException.ThrowIfNullOrWhiteSpace(change.ChangeId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(change.AnimeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(change.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(change.Reason);
        if (!Enum.IsDefined(change.Kind))
        {
            throw new ArgumentException("变更类型无效。", nameof(change));
        }

        if (change.ChangeId.Length > 100
            || change.Title.Length > 500
            || change.Reason.Length > 2_000)
        {
            throw new ArgumentException("变更标识、标题或理由过长。");
        }

        switch (change.Kind)
        {
            case PersonalAnimeChangeKind.SetTrackingStatus
                when change.TrackingStatus is null
                    || !Enum.IsDefined(change.TrackingStatus.Value):
                throw new ArgumentException("追番状态变更缺少目标状态。");
            case PersonalAnimeChangeKind.UpsertPlan
                when change.PlanPriority is null or < 0 or > 3:
                throw new ArgumentException("计划优先级必须位于 0–3。",
                    nameof(change));
            case PersonalAnimeChangeKind.ReplaceArchiveSummary
                or PersonalAnimeChangeKind.AppendArchiveEntry
                when string.IsNullOrWhiteSpace(change.Text)
                    || change.Text.Length > 20_000:
                throw new ArgumentException("档案文本为空或超过 20000 字符。");
        }

        if (change.EpisodeNumber is <= 0)
        {
            throw new ArgumentException("集数必须大于零。");
        }
    }

    private static string? Truncate(string? value, int maximumLength)
        => string.IsNullOrEmpty(value) || value.Length <= maximumLength
            ? value
            : value[..maximumLength];
}
