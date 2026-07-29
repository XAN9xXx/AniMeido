using AniMeido.Contracts;
using AniMeido.Contracts.Models;
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
    private readonly IAnimeDataSource _dataSource;
    private readonly TrackingService _tracking;
    private readonly ActionCenterService _actionCenter;
    private readonly PlanReminderCoordinator _reminders;
    private readonly BrowseHistoryService _browseHistory;

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
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var trackingRows = await _tracking.GetAllTrackingAsync();
            var statusById = trackingRows.ToDictionary(
                row => row.AnimeId,
                row => row.Status);
            var blockedIds = statusById
                .Where(pair => pair.Value == AnimeTrackingStatus.Blocked)
                .Select(pair => pair.Key)
                .ToHashSet();
            var (year, season) = SeasonHelper.GetCurrentSeason();
            var seasonal = (await _dataSource.GetAnimeBySeasonAsync(
                    year,
                    season,
                    cancellationToken))
                .Where(anime => !blockedIds.Contains(anime.ID))
                .ToList();
            var today = ToBangumiWeekday(DateTime.Today.DayOfWeek);
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

            await EnsureLegacyPlansAsync(
                trackingRows,
                seasonal,
                cancellationToken);
            var plans = await _actionCenter.GetPlansAsync(
                cancellationToken: cancellationToken);
            var reminders = await _actionCenter.GetRemindersAsync(
                state: PlanReminderState.Pending,
                cancellationToken: cancellationToken);
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

            await LoadBrowseHistoryAsync(blockedIds, cancellationToken);

            if (IsPlaybackAvailable)
            {
                await LoadPlaybackActivityAsync(
                    statusById,
                    seasonal,
                    cancellationToken);
            }
            else
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
            or InvalidOperationException
            or System.Text.Json.JsonException)
        {
            ErrorMessage = $"今天页加载失败：{ex.Message}";
        }
        finally
        {
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
        CancellationToken cancellationToken)
    {
        var records = await _browseHistory.GetHistoryAsync(
            8,
            cancellationToken);
        var entries = new List<TodayBrowseEntry>();
        foreach (var record in records)
        {
            if (blockedIds.Contains(record.AnimeId))
            {
                continue;
            }

            Anime? anime = null;
            try
            {
                anime = await _dataSource.GetAnimeDetailAsync(
                    record.AnimeId,
                    cancellationToken);
            }
            catch (HttpRequestException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.Text.Json.JsonException)
            {
            }
            catch (TaskCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
            }

            anime ??= new Anime(
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

        RecentBrowsed = new(entries);
    }

    private async Task LoadPlaybackActivityAsync(
        IReadOnlyDictionary<int, AnimeTrackingStatus> statusById,
        IReadOnlyList<Anime> seasonal,
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
        ContinueWatching = new ObservableCollection<TodayAnimeEntry>(
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
        var recentAnime = await ResolveAnimeAsync(
            visibleProgress.Keys.ToList(),
            seasonalById,
            cancellationToken);
        RecentActivity = new ObservableCollection<TodayAnimeEntry>(
            visibleProgress.Values
                .OrderByDescending(item => item.LastWatchedAt)
                .Take(8)
                .Join(
                    recentAnime,
                    item => item.AnimeId,
                    anime => anime.ID,
                    (item, anime) => new TodayAnimeEntry(
                        anime,
                        $"第 {item.CurrentEpisode} 集 · "
                            + $"{item.LastWatchedAt.LocalDateTime:g}")));
    }

    private async Task EnsureLegacyPlansAsync(
        IReadOnlyList<(
            int AnimeId,
            AnimeTrackingStatus Status,
            string UpdatedAt)> tracking,
        IReadOnlyList<Anime> seasonal,
        CancellationToken cancellationToken)
    {
        var currentPlans = await _actionCenter.GetPlansAsync(
            includeArchived: true,
            cancellationToken);
        var planIds = currentPlans.Select(item => item.AnimeId).ToHashSet();
        var seasonalById = seasonal.ToDictionary(item => item.ID);
        foreach (var row in tracking.Where(row =>
            row.Status == AnimeTrackingStatus.PlanToWatch
            && !planIds.Contains(row.AnimeId)))
        {
            var anime = seasonalById.GetValueOrDefault(row.AnimeId)
                ?? await _dataSource.GetAnimeDetailAsync(
                    row.AnimeId,
                    cancellationToken);
            await _actionCenter.UpsertPlanAsync(
                row.AnimeId,
                anime?.Title ?? $"Bangumi #{row.AnimeId}",
                AnimePlanPriority.Normal,
                targetStartDate: null,
                sortOrder: 0,
                cancellationToken);
        }
    }

    private async Task<IReadOnlyList<Anime>> ResolveAnimeAsync(
        IReadOnlyList<int> animeIds,
        IReadOnlyDictionary<int, Anime> seasonal,
        CancellationToken cancellationToken)
    {
        var result = new List<Anime>();
        foreach (var animeId in animeIds)
        {
            if (seasonal.TryGetValue(animeId, out var anime))
            {
                result.Add(anime);
                continue;
            }

            var detail = await _dataSource.GetAnimeDetailAsync(
                animeId,
                cancellationToken);
            if (detail is not null)
            {
                result.Add(detail);
            }
        }

        return result;
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

    private static int ToBangumiWeekday(DayOfWeek day)
        => day == DayOfWeek.Sunday ? 7 : (int)day;
}
