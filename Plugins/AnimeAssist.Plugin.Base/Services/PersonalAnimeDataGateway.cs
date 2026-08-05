using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Contracts.Notifications;
using AniMeido.Contracts.PersonalAnime;
using AniMeido.Plugin.Base.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace AniMeido.Plugin.Base.Services;

public sealed class PersonalAnimeDataGateway : IPersonalAnimeDataGateway
{
    private const int MaximumSelectionLimit = 200;
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
        IAppNotificationService notifications)
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
    }

    public async Task<IReadOnlyList<PersonalAnimeSelectionItem>>
        QuerySelectionAsync(
            PersonalAnimeSelectionQuery query,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var limit = Math.Clamp(query.Limit, 1, MaximumSelectionLimit);
        var tracking = await _tracking.GetAllTrackingAsync();
        var plans = await _actionCenter.GetPlansAsync(
            includeArchived: false,
            cancellationToken);
        var archives = await _archive.GetArchiveListAsync(cancellationToken);
        var history = await _browseHistory.GetHistoryAsync(
            MaximumSelectionLimit,
            cancellationToken);
        var planById = plans.ToDictionary(item => item.AnimeId);
        var archiveById = archives.ToDictionary(
            item => item.Archive.AnimeId);
        var trackingById = tracking.ToDictionary(
            item => item.AnimeId);
        var historyById = history.ToDictionary(item => item.AnimeId);
        var ids = trackingById.Keys
            .Concat(planById.Keys)
            .Concat(archiveById.Keys)
            .Concat(historyById.Keys)
            .Distinct();
        var statuses = query.TrackingStatuses?.ToHashSet();
        var search = query.SearchText?.Trim();
        var result = new List<PersonalAnimeSelectionItem>();
        foreach (var animeId in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trackingById.TryGetValue(animeId, out var trackingItem);
            planById.TryGetValue(animeId, out var plan);
            archiveById.TryGetValue(animeId, out var archive);
            historyById.TryGetValue(animeId, out var browse);
            AnimeTrackingStatus? status = trackingItem == default
                ? null
                : trackingItem.Status;
            if (query.PlansOnly && plan is null
                || query.ArchivesOnly && archive is null
                || statuses is { Count: > 0 }
                    && (status is null || !statuses.Contains(status.Value)))
            {
                continue;
            }

            var title = archive?.Archive.TitleSnapshot
                ?? plan?.TitleSnapshot
                ?? browse.Title
                ?? $"Bangumi #{animeId}";
            if (!string.IsNullOrWhiteSpace(search)
                && !title.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var updatedAt = ParseTimestampOrDefault(
                trackingItem == default ? null : trackingItem.UpdatedAt,
                plan?.UpdatedAt
                    ?? archive?.Archive.UpdatedAt
                    ?? (browse == default
                        ? DateTimeOffset.MinValue
                        : new DateTimeOffset(browse.LastViewed)));
            result.Add(new PersonalAnimeSelectionItem(
                animeId,
                title,
                status,
                plan is not null,
                archive is not null,
                archive?.Archive.PersonalRating,
                updatedAt));
        }

        return result
            .OrderByDescending(item => item.UpdatedAt)
            .Take(limit)
            .ToArray();
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

        var tracking = (await _tracking.GetAllTrackingAsync())
            .ToDictionary(item => item.AnimeId);
        var plans = (await _actionCenter.GetPlansAsync(
            includeArchived: true,
            cancellationToken)).ToDictionary(item => item.AnimeId);
        var progress = await _actionCenter.GetProgressAsync(
            cancellationToken);
        var browse = (await _browseHistory.GetHistoryAsync(
            MaximumSelectionLimit,
            cancellationToken)).ToDictionary(item => item.AnimeId);
        var items = new List<PersonalAnimeContextItem>(animeIds.Length);
        foreach (var animeId in animeIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(await BuildItemAsync(
                animeId,
                request.Categories,
                tracking,
                plans,
                progress,
                browse,
                cancellationToken));
        }

        var savedTags = request.Categories.HasFlag(
            PersonalAnimeDataCategory.SavedBangumiTags)
                ? await _savedTags.GetAllSavedTagsAsync()
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
                results.Add(new PersonalAnimeChangeResult(
                    change.ChangeId,
                    false,
                    false,
                    ex.Message));
            }
        }

        return new PersonalAnimeChangeApplyResult(results);
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
                ?? $"Bangumi #{animeId}",
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
        var message = "变更已应用。";
        if (change.Kind == PersonalAnimeChangeKind.UpsertPlan)
        {
            var plan = await _actionCenter.GetPlanAsync(
                change.AnimeId,
                cancellationToken);
            if (plan is not null)
            {
                try
                {
                    await _reminders.RescheduleAnimeAsync(
                        plan,
                        cancellationToken);
                }
                catch (Exception ex) when (
                    ex is InvalidOperationException
                        or IOException
                        or COMException)
                {
                    message = $"计划已更新，但提醒重排失败：{ex.Message}";
                }
            }
        }
        else if (change.Kind == PersonalAnimeChangeKind.SetTrackingStatus
            && change.TrackingStatus != AnimeTrackingStatus.PlanToWatch)
        {
            try
            {
                await _notifications.CancelGroupAsync(
                    PlanReminderCoordinator.GetNotificationGroup(
                        change.AnimeId),
                    cancellationToken);
            }
            catch (Exception ex) when (
                ex is InvalidOperationException
                    or IOException
                    or COMException)
            {
                message = $"状态已更新，但旧计划通知取消失败：{ex.Message}";
            }
        }

        return new PersonalAnimeChangeResult(
            change.ChangeId,
            true,
            false,
            message);
    }

    private static async Task ApplyDomainChangeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PersonalAnimeChange change,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@animeId", change.AnimeId);
        command.Parameters.AddWithValue("@title", change.Title.Trim());
        command.Parameters.AddWithValue("@now", now);
        switch (change.Kind)
        {
            case PersonalAnimeChangeKind.SetTrackingStatus:
                command.CommandText = """
                    INSERT INTO tracking_events(
                        EventId, AnimeId, PreviousStatus, NewStatus, ChangedAt)
                    VALUES(
                        @eventId,
                        @animeId,
                        (SELECT Status FROM tracking WHERE AnimeId = @animeId),
                        @status,
                        @now);
                    INSERT INTO tracking(AnimeId, Status, UpdatedAt)
                    VALUES(@animeId, @status, @now)
                    ON CONFLICT(AnimeId) DO UPDATE SET
                        Status = excluded.Status,
                        UpdatedAt = excluded.UpdatedAt;
                    UPDATE anime_plans
                    SET ArchivedAt = CASE
                            WHEN @status = @planToWatch THEN NULL
                            ELSE COALESCE(ArchivedAt, @now)
                        END,
                        StartedAt = CASE
                            WHEN @status = @planToWatch THEN NULL
                            ELSE StartedAt
                        END,
                        UpdatedAt = @now
                    WHERE AnimeId = @animeId;
                    UPDATE plan_reminders
                    SET State = @cancelled
                    WHERE AnimeId = @animeId
                      AND State = @pending
                      AND @status <> @planToWatch;
                    """;
                command.Parameters.AddWithValue("@eventId", change.ChangeId);
                command.Parameters.AddWithValue(
                    "@status",
                    (int)change.TrackingStatus!.Value);
                command.Parameters.AddWithValue(
                    "@planToWatch",
                    (int)AnimeTrackingStatus.PlanToWatch);
                command.Parameters.AddWithValue(
                    "@cancelled",
                    (int)PlanReminderState.Cancelled);
                command.Parameters.AddWithValue(
                    "@pending",
                    (int)PlanReminderState.Pending);
                break;
            case PersonalAnimeChangeKind.UpsertPlan:
                command.CommandText = """
                    INSERT INTO anime_plans(
                        AnimeId, TitleSnapshot, Priority, TargetStartDate,
                        SortOrder, CreatedAt, UpdatedAt, StartedAt, ArchivedAt)
                    VALUES(
                        @animeId, @title, @priority, @targetDate,
                        0, @now, @now, NULL, NULL)
                    ON CONFLICT(AnimeId) DO UPDATE SET
                        TitleSnapshot = excluded.TitleSnapshot,
                        Priority = excluded.Priority,
                        TargetStartDate = excluded.TargetStartDate,
                        UpdatedAt = excluded.UpdatedAt,
                        ArchivedAt = NULL;
                    """;
                command.Parameters.AddWithValue(
                    "@priority",
                    change.PlanPriority!.Value);
                command.Parameters.AddWithValue(
                    "@targetDate",
                    change.PlanTargetStartDate?.ToString(
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
                break;
            case PersonalAnimeChangeKind.ReplaceArchiveSummary:
                command.CommandText = """
                    INSERT INTO anime_archives(
                        AnimeId, TitleSnapshot, PersonalRating, SummaryNote,
                        CreatedAt, UpdatedAt)
                    VALUES(@animeId, @title, NULL, @text, @now, @now)
                    ON CONFLICT(AnimeId) DO UPDATE SET
                        TitleSnapshot = excluded.TitleSnapshot,
                        SummaryNote = excluded.SummaryNote,
                        UpdatedAt = excluded.UpdatedAt;
                    """;
                command.Parameters.AddWithValue("@text", change.Text!.Trim());
                break;
            case PersonalAnimeChangeKind.AppendArchiveEntry:
                command.CommandText = """
                    INSERT INTO anime_archives(
                        AnimeId, TitleSnapshot, PersonalRating, SummaryNote,
                        CreatedAt, UpdatedAt)
                    VALUES(@animeId, @title, NULL, '', @now, @now)
                    ON CONFLICT(AnimeId) DO UPDATE SET
                        TitleSnapshot = excluded.TitleSnapshot,
                        UpdatedAt = excluded.UpdatedAt;
                    INSERT INTO archive_entries(
                        EntryId, AnimeId, OccurredAt, EpisodeNumber, Body,
                        CreatedAt, UpdatedAt)
                    VALUES(
                        @entryId, @animeId, @occurredAt, @episode,
                        @text, @now, @now);
                    """;
                command.Parameters.AddWithValue("@entryId", change.ChangeId);
                command.Parameters.AddWithValue(
                    "@occurredAt",
                    (change.OccurredAt ?? DateTimeOffset.UtcNow)
                        .ToUniversalTime()
                        .ToString("O"));
                command.Parameters.AddWithValue(
                    "@episode",
                    change.EpisodeNumber is null
                        ? DBNull.Value
                        : change.EpisodeNumber.Value);
                command.Parameters.AddWithValue("@text", change.Text!.Trim());
                break;
            default:
                throw new InvalidOperationException("不支持的变更类型。");
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
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

    private static DateTimeOffset ParseTimestampOrDefault(
        string? primary,
        DateTimeOffset fallback)
        => DateTimeOffset.TryParse(
            primary,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
                ? parsed
                : fallback;

    private static string? Truncate(string? value, int maximumLength)
        => string.IsNullOrEmpty(value) || value.Length <= maximumLength
            ? value
            : value[..maximumLength];
}
