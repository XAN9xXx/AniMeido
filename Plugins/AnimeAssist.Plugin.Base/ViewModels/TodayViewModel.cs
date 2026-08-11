using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace AniMeido.Plugin.Base.ViewModels;

public sealed record TodayAnimeEntry(
    Anime Anime,
    string Subtitle);

public sealed record TodayBrowseEntry(
    Anime Anime,
    string Subtitle);

public sealed record TodayPlanEntry(
    AnimePlan Plan,
    string Subtitle)
{
    public string Title => Plan.TitleSnapshot;

    public string PriorityText => Plan.Priority switch
    {
        AnimePlanPriority.Critical => "最高",
        AnimePlanPriority.High => "高",
        AnimePlanPriority.Normal => "普通",
        _ => "低",
    };
}

public partial class TodayViewModel : ObservableObject
{
    private static readonly SemaphoreSlim LegacyPlanGate = new(1, 1);
    private static bool _legacyPlansInitialized;
    private readonly IAnimeDataSource _dataSource;
    private readonly TrackingService _tracking;
    private readonly ActionCenterService _actionCenter;
    private readonly PlanReminderCoordinator _reminders;
    private readonly BrowseHistoryService _browseHistory;
    private int _loadVersion;

    [ObservableProperty]
    private ObservableCollection<Anime> _personalBroadcasts = [];

    [ObservableProperty]
    private ObservableCollection<Anime> _allBroadcasts = [];

    [ObservableProperty]
    private ObservableCollection<TodayAnimeEntry> _continueWatching = [];

    [ObservableProperty]
    private ObservableCollection<TodayPlanEntry> _plans = [];

    [ObservableProperty]
    private ObservableCollection<TodayAnimeEntry> _recentActivity = [];

    [ObservableProperty]
    private ObservableCollection<TodayBrowseEntry> _recentBrowsed = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isPlaybackAvailable;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _notificationMessage;

    public TodayViewModel(
        IAnimeDataSource dataSource,
        TrackingService tracking,
        ActionCenterService actionCenter,
        PlanReminderCoordinator reminders,
        BrowseHistoryService browseHistory)
    {
        _dataSource = dataSource;
        _tracking = tracking;
        _actionCenter = actionCenter;
        _reminders = reminders;
        _browseHistory = browseHistory;
    }

    public string TodayLabel => DateTime.Today.ToString(
        "M月d日 dddd",
        System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var loadVersion = Interlocked.Increment(ref _loadVersion);
        OnPropertyChanged(nameof(TodayLabel));
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var trackingTask = _tracking.GetAllTrackingAsync();
            var scheduleTask = _dataSource.GetCurrentBroadcastScheduleAsync(
                cancellationToken);
            var trackingRows = await trackingTask;
            if (!IsCurrentLoad(loadVersion, cancellationToken))
                return;

            var statusById = trackingRows.ToDictionary(
                row => row.AnimeId,
                row => row.Status);
            var blockedIds = statusById
                .Where(pair => pair.Value == AnimeTrackingStatus.Blocked)
                .Select(pair => pair.Key)
                .ToHashSet();
            var plansTask = _actionCenter.GetPlansAsync(
                cancellationToken: cancellationToken);
            var remindersTask = _actionCenter.GetRemindersAsync(
                state: PlanReminderState.Pending,
                cancellationToken: cancellationToken);
            var browseTask = LoadBrowseHistoryAsync(
                blockedIds,
                loadVersion,
                cancellationToken);
            await Task.WhenAll(plansTask, remindersTask);
            if (!IsCurrentLoad(loadVersion, cancellationToken))
                return;

            var plans = await plansTask;
            var reminders = await remindersTask;
            var reminderCountByAnime = reminders
                .GroupBy(item => item.AnimeId)
                .ToDictionary(group => group.Key, group => group.Count());
            Plans = new ObservableCollection<TodayPlanEntry>(
                plans
                    .Where(plan => !blockedIds.Contains(plan.AnimeId))
                    .Select(plan => new TodayPlanEntry(
                    plan,
                    BuildPlanSubtitle(
                     plan,
                     reminderCountByAnime.GetValueOrDefault(
                         plan.AnimeId)))));

            IReadOnlyList<Anime> seasonal;
            try
            {
                seasonal = AnimeListPresentation.Filter(
                    await scheduleTask,
                    blockedIds);
            }
            catch (Exception ex) when (
                ex is HttpRequestException
                or BangumiApiException
                or InvalidOperationException
                or System.Text.Json.JsonException)
            {
                seasonal = [];
                ErrorMessage = $"放送数据加载失败：{ex.Message}";
            }

            if (!IsCurrentLoad(loadVersion, cancellationToken))
                return;

            var today = AnimeListPresentation.ToBangumiWeekday(
                DateTime.Today.DayOfWeek);
            AllBroadcasts = new ObservableCollection<Anime>(
                seasonal.Where(item => item.Weekday == today));
            var personalIds = statusById
                .Where(pair => pair.Value is
                    AnimeTrackingStatus.Watching
                    or AnimeTrackingStatus.PlanToWatch
                    or AnimeTrackingStatus.Following)
                .Select(pair => pair.Key)
                .ToHashSet();
            PersonalBroadcasts = new ObservableCollection<Anime>(
                AllBroadcasts.Where(item => personalIds.Contains(item.ID)));

            await EnsureLegacyPlansOnceAsync(
                trackingRows,
                seasonal,
                cancellationToken);
            var playbackTask = IsPlaybackAvailable
                ? LoadPlaybackActivityAsync(
                    statusById,
                    seasonal,
                    loadVersion,
                    cancellationToken)
                : Task.CompletedTask;
            await Task.WhenAll(browseTask, playbackTask);
            if (!IsCurrentLoad(loadVersion, cancellationToken))
                return;

            if (!IsPlaybackAvailable)
            {
                ContinueWatching.Clear();
                RecentActivity.Clear();
            }

            try
            {
                await _reminders.ReconcileAsync(cancellationToken);
                NotificationMessage = _reminders.NotificationsAvailable
                    ? null
                    : "Windows 通知当前不可用，计划仍会在今天页显示。";
            }
            catch (InvalidOperationException ex)
            {
                NotificationMessage = ex.Message;
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (
            ex is HttpRequestException
            or BangumiApiException
            or InvalidOperationException
            or System.Text.Json.JsonException)
        {
            ErrorMessage = $"今天页加载失败：{ex.Message}";
        }
        finally
        {
            if (loadVersion == _loadVersion)
                IsLoading = false;
        }
    }

    public async Task RemoveBlockedEntriesAsync()
    {
        var blocked = await _tracking.GetBlockedAnimeIdsAsync();
        if (blocked.Count == 0)
        {
            return;
        }

        AllBroadcasts = new ObservableCollection<Anime>(
            AllBroadcasts.Where(anime => !blocked.Contains(anime.ID)));
        PersonalBroadcasts = new ObservableCollection<Anime>(
            PersonalBroadcasts.Where(anime => !blocked.Contains(anime.ID)));
        ContinueWatching = new ObservableCollection<TodayAnimeEntry>(
            ContinueWatching.Where(
                entry => !blocked.Contains(entry.Anime.ID)));
        RecentActivity = new ObservableCollection<TodayAnimeEntry>(
            RecentActivity.Where(
                entry => !blocked.Contains(entry.Anime.ID)));
        RecentBrowsed = new ObservableCollection<TodayBrowseEntry>(
            RecentBrowsed.Where(
                entry => !blocked.Contains(entry.Anime.ID)));
        Plans = new ObservableCollection<TodayPlanEntry>(
            Plans.Where(entry => !blocked.Contains(entry.Plan.AnimeId)));
    }

    [RelayCommand]
    public async Task ClearBrowseHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        await _browseHistory.ClearAsync(cancellationToken);
        RecentBrowsed.Clear();
    }

    private async Task LoadBrowseHistoryAsync(
        IReadOnlySet<int> blockedIds,
        int loadVersion,
        CancellationToken cancellationToken)
    {
        var records = await _browseHistory.GetHistoryAsync(
            8,
            cancellationToken);
        var visibleRecords = records
            .Where(record => !blockedIds.Contains(record.AnimeId))
            .ToList();
        var resolved = (await ResolveAnimeAsync(
                visibleRecords.Select(record => record.AnimeId).ToList(),
                new Dictionary<int, Anime>(),
                cancellationToken))
            .ToDictionary(anime => anime.ID);
        var entries = new List<TodayBrowseEntry>();
        foreach (var record in visibleRecords)
        {
            var anime = resolved.GetValueOrDefault(record.AnimeId)
                ?? new Anime(
                record.AnimeId,
                record.Title ?? $"#{record.AnimeId}",
                null,
                [],
                null,
                null,
                string.Empty,
                0,
                0);
            entries.Add(new TodayBrowseEntry(
                anime,
                $"{record.LastViewed.ToLocalTime():g} · "
                    + $"浏览 {record.ViewCount} 次"));
        }

        if (IsCurrentLoad(loadVersion, cancellationToken))
            RecentBrowsed = new(entries);
    }

    private async Task LoadPlaybackActivityAsync(
        IReadOnlyDictionary<int, AnimeTrackingStatus> statusById,
        IReadOnlyList<Anime> seasonal,
        int loadVersion,
        CancellationToken cancellationToken)
    {
        var progress = await _actionCenter.GetProgressAsync(
            cancellationToken);
        var seasonalById = seasonal.ToDictionary(item => item.ID);
        var watchingIds = statusById
            .Where(pair => pair.Value == AnimeTrackingStatus.Watching)
            .Select(pair => pair.Key)
            .ToList();
        var watchingAnime = await ResolveAnimeAsync(
            watchingIds,
            seasonalById,
            cancellationToken);
        var continueWatching = new ObservableCollection<TodayAnimeEntry>(
            watchingAnime.Select(anime =>
            {
                progress.TryGetValue(anime.ID, out var snapshot);
                return new TodayAnimeEntry(
                    anime,
                    snapshot is null
                        ? "尚未记录观看进度"
                        : $"看到第 {snapshot.CurrentEpisode} 集 · "
                            + $"{snapshot.LastWatchedAt.LocalDateTime:g}");
            })
            .OrderByDescending(item =>
                progress.GetValueOrDefault(item.Anime.ID)?.LastWatchedAt));
        var visibleProgress = progress
            .Where(pair => statusById.GetValueOrDefault(pair.Key)
                != AnimeTrackingStatus.Blocked)
            .ToDictionary();
        var recentProgress = visibleProgress.Values
            .OrderByDescending(item => item.LastWatchedAt)
            .Take(8)
            .ToList();
        var recentAnime = await ResolveAnimeAsync(
            recentProgress.Select(item => item.AnimeId).ToList(),
            seasonalById,
            cancellationToken);
        var recentActivity = new ObservableCollection<TodayAnimeEntry>(
            recentProgress
                .Join(
                    recentAnime,
                    item => item.AnimeId,
                    anime => anime.ID,
                    (item, anime) => new TodayAnimeEntry(
                        anime,
                        $"第 {item.CurrentEpisode} 集 · "
                            + $"{item.LastWatchedAt.LocalDateTime:g}")));
        if (IsCurrentLoad(loadVersion, cancellationToken))
        {
            ContinueWatching = continueWatching;
            RecentActivity = recentActivity;
        }
    }

    private bool IsCurrentLoad(
        int loadVersion,
        CancellationToken cancellationToken)
        => !cancellationToken.IsCancellationRequested
            && loadVersion == _loadVersion;

    private async Task EnsureLegacyPlansOnceAsync(
        IReadOnlyList<(
            int AnimeId,
            AnimeTrackingStatus Status,
            string UpdatedAt)> tracking,
        IReadOnlyList<Anime> seasonal,
        CancellationToken cancellationToken)
    {
        if (_legacyPlansInitialized)
            return;

        await LegacyPlanGate.WaitAsync(cancellationToken);
        try
        {
            if (_legacyPlansInitialized)
                return;

            var currentPlans = await _actionCenter.GetPlansAsync(
                includeArchived: true,
                cancellationToken);
            var planIds = currentPlans.Select(item => item.AnimeId).ToHashSet();
            var rows = tracking.Where(row =>
                    row.Status == AnimeTrackingStatus.PlanToWatch
                    && !planIds.Contains(row.AnimeId))
                .ToList();
            var seasonalById = seasonal.ToDictionary(item => item.ID);
            var resolved = (await ResolveAnimeAsync(
                    rows.Select(row => row.AnimeId).ToList(),
                    seasonalById,
                    cancellationToken))
                .ToDictionary(anime => anime.ID);
            foreach (var row in rows)
            {
                await _actionCenter.UpsertPlanAsync(
                    row.AnimeId,
                    resolved.GetValueOrDefault(row.AnimeId)?.Title
                        ?? $"Bangumi #{row.AnimeId}",
                    AnimePlanPriority.Normal,
                    targetStartDate: null,
                    sortOrder: 0,
                    cancellationToken);
            }

            _legacyPlansInitialized = true;
        }
        finally
        {
            LegacyPlanGate.Release();
        }
    }

    private async Task<IReadOnlyList<Anime>> ResolveAnimeAsync(
        IReadOnlyList<int> animeIds,
        IReadOnlyDictionary<int, Anime> seasonal,
        CancellationToken cancellationToken)
    {
        var distinctIds = animeIds.Distinct().ToList();
        var result = new Anime?[distinctIds.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, distinctIds.Count),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 4,
            },
            async (index, token) =>
        {
            var animeId = distinctIds[index];
            if (seasonal.TryGetValue(animeId, out var anime))
            {
                result[index] = anime;
                return;
            }

            try
            {
                result[index] = await _dataSource.GetAnimeDetailAsync(
                    animeId,
                    token);
            }
            catch (Exception ex) when (
                ex is HttpRequestException
                or InvalidOperationException
                or System.Text.Json.JsonException
                or TaskCanceledException)
            {
                if (ex is OperationCanceledException
                    && token.IsCancellationRequested)
                {
                    throw;
                }
            }
        });

        return result.OfType<Anime>().ToList();
    }

    private static string BuildPlanSubtitle(
        AnimePlan plan,
        int reminderCount)
    {
        var reminderText = reminderCount == 0
            ? "无提醒"
            : $"{reminderCount} 个提醒";
        if (plan.TargetStartDate is null)
        {
            return $"未设置目标日期 · {reminderText}";
        }

        var days = plan.TargetStartDate.Value.DayNumber
            - DateOnly.FromDateTime(DateTime.Today).DayNumber;
        var dateText = days switch
        {
            < 0 => $"已逾期 {-days} 天",
            0 => "计划今天开始",
            <= 7 => $"{days} 天后开始",
            _ => $"目标 {plan.TargetStartDate:yyyy-MM-dd}",
        };
        return $"{dateText} · {reminderText}";
    }

}
