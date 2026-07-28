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
        PlanReminderCoordinator reminders)
    {
        _dataSource = dataSource;
        _tracking = tracking;
        _actionCenter = actionCenter;
        _reminders = reminders;
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
            var (year, season) = SeasonHelper.GetCurrentSeason();
            var seasonal = await _dataSource.GetAnimeBySeasonAsync(
                year,
                season,
                cancellationToken);
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
                plans.Select(plan => new TodayPlanEntry(
                    plan,
                    BuildPlanSubtitle(
                        plan,
                        reminderCountByAnime.GetValueOrDefault(
                            plan.AnimeId)))));

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
        var recentAnime = await ResolveAnimeAsync(
            progress.Keys.ToList(),
            seasonalById,
            cancellationToken);
        RecentActivity = new ObservableCollection<TodayAnimeEntry>(
            progress.Values
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
