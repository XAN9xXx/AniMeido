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
    string Subtitle)
{
    public double ProgressPercent { get; init; }
    public bool HasProgress { get; init; }

    /// <summary>个人状态徽标，例如“追番中”；为空时不显示。</summary>
    public string Badge { get; init; } = "";
    public bool HasBadge => Badge.Length > 0;

    /// <summary>行尾的时间说明，例如“昨天 21:30”；为空时不显示。</summary>
    public string Trailing { get; init; } = "";
    public bool HasTrailing => Trailing.Length > 0;
}

public sealed record TodayBroadcastDay(string Label, IReadOnlyList<Anime> Items)
{
    public bool IsToday { get; init; }
    public bool IsOtherDay => !IsToday;
    public string CountText => IsToday
        ? (Items.Count == 0 ? "今天" : $"今天 · {Items.Count} 部")
        : (Items.Count == 0 ? "—" : $"{Items.Count} 部");
    public IReadOnlyList<string> PreviewTitles => Items.Take(2).Select(item => item.Title).ToList();
    public bool HasMore => Items.Count > 2;
    public string MoreText => $"等 {Items.Count} 部";
}

public sealed record TodayBrowseEntry(
    Anime Anime,
    string Subtitle);

public sealed record TodayPlanEntry(
    AnimePlan Plan,
    string Subtitle)
{
    public string Title => Plan.TitleSnapshot;
    public Anime? Anime { get; init; }
    public string GroupHeader { get; init; } = "";
    public bool IsOverdue { get; init; }
    public bool HasGroupHeader => GroupHeader.Length > 0;
    public bool HasSubtitle => Subtitle.Length > 0;
    public bool HasPriority => Plan.Priority is AnimePlanPriority.High or AnimePlanPriority.Critical;

    public string PriorityText => Plan.Priority switch
    {
        AnimePlanPriority.Critical => "最高",
        AnimePlanPriority.High => "高",
        AnimePlanPriority.Normal => "普通",
        _ => "低",
    };

    /// <summary>日期、提醒与较高优先级合成一行；默认值不显示。</summary>
    public string Meta => string.Join(
        " · ",
        new[] { Subtitle, HasPriority ? $"{PriorityText}优先级" : "" }
            .Where(part => part.Length > 0));
    public bool HasNormalMeta => !IsOverdue && Meta.Length > 0;
    public bool HasOverdueMeta => IsOverdue && Meta.Length > 0;
}

public partial class TodayViewModel : ObservableObject
{
    private static readonly SemaphoreSlim MissingPlanGate = new(1, 1);
    private readonly IAnimeDataSource _dataSource;
    private readonly TrackingService _tracking;
    private readonly ActionCenterService _actionCenter;
    private readonly PlanReminderCoordinator _reminders;
    private readonly BrowseHistoryService _browseHistory;
    private int _loadVersion;
    private IReadOnlyList<Anime> _seasonal = [];
    private HashSet<int> _personalIds = [];
    private bool _scheduleFailed;

    // 复用详情页的状态文案，避免“追番中 / 补番中 / 关注中”在两处各写一份。
    private static readonly IReadOnlyDictionary<AnimeTrackingStatus, string> StatusLabels =
        TrackingActionDescriptor.CreateDefaults()
            .ToDictionary(action => action.Status, action => action.ActiveLabel);

    [ObservableProperty]
    private ObservableCollection<TodayBroadcastDay> _broadcastDays = [];

    [ObservableProperty]
    private string _broadcastMessage = "正在加载放送日程…";

    [ObservableProperty]
    private string _nextBroadcastText = "";

    public bool HasNoBroadcasts => PersonalBroadcasts.Count == 0;
    public bool HasBroadcasts => PersonalBroadcasts.Count > 0;
    // 今天的放送卡片：一部时占满左栏，两部及以上每行两张。
    public int BroadcastColumns => Math.Clamp(PersonalBroadcasts.Count, 1, 2);
    public bool HasBroadcastDays => BroadcastDays.Count > 0;
    public bool HasNextBroadcast => NextBroadcastText.Length > 0;
    public bool HasNoPlans => Plans.Count == 0;
    public bool HasNoRecentActivity => RecentActivity.Count == 0;
    public bool HasUnstarted => ContinueWatching.Count > 0;
    public bool HasNoBrowsed => RecentBrowsed.Count == 0;
    public bool HasBrowsed => RecentBrowsed.Count > 0;
    public bool HasNotification => !string.IsNullOrWhiteSpace(NotificationMessage);
    public string PlanCountText => $"{Plans.Count} 部 · 按计划日期排列";
    public bool HasOverflowPlans => OverflowPlans.Count > 0;
    public string PlanStackHint => $"还有 {OverflowPlans.Count} 项";
    public bool HasMultipleOverflowPlans => OverflowPlans.Count > 1;
    public bool HasThirdStackCard => OverflowPlans.Count > 2;
    public string FirstStackTitle => OverflowPlans.ElementAtOrDefault(0)?.Title ?? "";
    public string SecondStackTitle => OverflowPlans.ElementAtOrDefault(1)?.Title ?? "";
    public string ThirdStackTitle => OverflowPlans.ElementAtOrDefault(2)?.Title ?? "";

    partial void OnPersonalBroadcastsChanged(ObservableCollection<TodayAnimeEntry> value)
    {
        OnPropertyChanged(nameof(HasNoBroadcasts));
        OnPropertyChanged(nameof(HasBroadcasts));
        OnPropertyChanged(nameof(BroadcastColumns));
    }
    partial void OnBroadcastDaysChanged(ObservableCollection<TodayBroadcastDay> value) => OnPropertyChanged(nameof(HasBroadcastDays));
    partial void OnNextBroadcastTextChanged(string value) => OnPropertyChanged(nameof(HasNextBroadcast));
    partial void OnPlansChanged(ObservableCollection<TodayPlanEntry> value) => UpdatePlanSummary();

    /// <summary>计划数量与卡包后排；原地替换条目后也要调用。</summary>
    private void UpdatePlanSummary()
    {
        OnPropertyChanged(nameof(HasNoPlans));
        OnPropertyChanged(nameof(PlanCountText));
        var (_, overflow) = SplitPlans(Plans);
        OverflowPlans = new(overflow);
        OnPropertyChanged(nameof(HasOverflowPlans));
        OnPropertyChanged(nameof(HasMultipleOverflowPlans));
        OnPropertyChanged(nameof(PlanStackHint));
        OnPropertyChanged(nameof(HasThirdStackCard));
        OnPropertyChanged(nameof(FirstStackTitle));
        OnPropertyChanged(nameof(SecondStackTitle));
        OnPropertyChanged(nameof(ThirdStackTitle));
    }
    partial void OnRecentActivityChanged(ObservableCollection<TodayAnimeEntry> value) => OnPropertyChanged(nameof(HasNoRecentActivity));
    partial void OnContinueWatchingChanged(ObservableCollection<TodayAnimeEntry> value) => OnPropertyChanged(nameof(HasUnstarted));
    partial void OnRecentBrowsedChanged(ObservableCollection<TodayBrowseEntry> value)
    {
        OnPropertyChanged(nameof(HasNoBrowsed));
        OnPropertyChanged(nameof(HasBrowsed));
    }
    partial void OnNotificationMessageChanged(string? value) => OnPropertyChanged(nameof(HasNotification));

    [ObservableProperty]
    private ObservableCollection<TodayAnimeEntry> _personalBroadcasts = [];

    [ObservableProperty]
    private ObservableCollection<Anime> _allBroadcasts = [];

    [ObservableProperty]
    private ObservableCollection<TodayAnimeEntry> _continueWatching = [];

    [ObservableProperty]
    private ObservableCollection<TodayPlanEntry> _plans = [];

    [ObservableProperty]
    private ObservableCollection<TodayPlanEntry> _overflowPlans = [];

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
        BrowseHistoryService browseHistory,
        ArchiveService archive)
    {
        _dataSource = dataSource;
        _tracking = tracking;
        _actionCenter = actionCenter;
        _reminders = reminders;
        _browseHistory = browseHistory;
        Theme = new TodayThemeViewModel(dataSource, tracking, browseHistory, archive, actionCenter);
    }

    /// <summary>今日主题：单独加载，慢或失败都不影响今天页其他部分。</summary>
    public TodayThemeViewModel Theme { get; }

    internal const int VisiblePlanLimit = 3;

    // 只切分视图，不轮换、不复制条目，也不改变原计划顺序。
    internal static (IReadOnlyList<TodayPlanEntry> Visible, IReadOnlyList<TodayPlanEntry> Overflow)
        SplitPlans(IReadOnlyList<TodayPlanEntry> plans)
        => (plans.Take(VisiblePlanLimit).ToArray(), plans.Skip(VisiblePlanLimit).ToArray());

    public string TodayLabel => DateTime.Today.ToString(
        "M月d日 dddd",
        System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var loadVersion = Interlocked.Increment(ref _loadVersion);
        IsLoadInterrupted = true;
        OnPropertyChanged(nameof(TodayLabel));
        _ = Theme.LoadAsync();
        IsLoading = true;
        ErrorMessage = null;
        _scheduleFailed = false;
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
            var todayDate = DateOnly.FromDateTime(DateTime.Today);
            // 本地计划先显示，不等放送日程与作品详情；刷新时沿用已加载的封面，缺失的用占位。
            var knownPlanAnime = KnownPlanAnime();
            ApplyPlanEntries(BuildPlanEntries(
                plans.Where(plan => !blockedIds.Contains(plan.AnimeId)),
                reminderCountByAnime,
                knownPlanAnime,
                todayDate));

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
                _scheduleFailed = true;
                ErrorMessage = $"放送数据加载失败：{ex.Message}";
            }

            if (!IsCurrentLoad(loadVersion, cancellationToken))
                return;

            var today = AnimeListPresentation.ToBangumiWeekday(
                DateTime.Today.DayOfWeek);
            AllBroadcasts = new ObservableCollection<Anime>(
                seasonal.Where(item => item.Weekday == today));
            _seasonal = seasonal;
            ApplyPersonalStatuses(statusById);

            await EnsureMissingPlansAsync(
                trackingRows,
                seasonal,
                cancellationToken);
            // Re-read after legacy plan creation, then reuse the existing bounded detail resolver for covers.
            plans = await _actionCenter.GetPlansAsync(cancellationToken: cancellationToken);
            var visiblePlans = plans.Where(plan => !blockedIds.Contains(plan.AnimeId)).ToList();
            // 只补还没有封面的计划；单部作品详情失败只缺这一张封面（ResolveAnimeAsync 内部降级）。
            var planAnime = await ResolvePlanAnimeAsync(visiblePlans, knownPlanAnime, cancellationToken);
            if (!IsCurrentLoad(loadVersion, cancellationToken)) return;
            ApplyPlanEntries(BuildPlanEntries(visiblePlans, reminderCountByAnime, planAnime, todayDate));
            var playbackTask = IsPlaybackAvailable
                ? LoadPlaybackActivityAsync(
                    statusById,
                    seasonal,
                    plans.Select(plan => plan.AnimeId).ToHashSet(),
                    loadVersion,
                    cancellationToken)
                : Task.CompletedTask;
            await Task.WhenAll(browseTask, playbackTask);
            if (!IsCurrentLoad(loadVersion, cancellationToken))
                return;

            if (!IsPlaybackAvailable)
            {
                ContinueWatching = [];
                RecentActivity = [];
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

            MarkLoadFinished(loadVersion);
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
            // 失败已经提示，由用户点刷新重试，返回页面时不自动重来。
            MarkLoadFinished(loadVersion);
        }
        finally
        {
            if (loadVersion == _loadVersion)
                IsLoading = false;
        }
    }

    /// <summary>
    /// 最近一次整页加载没有走完（例如离开页面时被取消）。页面按原实例返回时据此重新加载。
    /// </summary>
    public bool IsLoadInterrupted { get; private set; }

    private void MarkLoadFinished(int loadVersion)
    {
        // 被新一次加载取代的旧加载不能替新加载报告完成。
        if (loadVersion == _loadVersion)
            IsLoadInterrupted = false;
    }

    /// <summary>
    /// 在今日主题里改了标记之后调用：只重读标记、计划与提醒，刷新补番计划、今日放送与“还没开始看”。
    /// 放送日程与封面沿用本次已加载的，不重跑今日主题、浏览记录、播放记录与提醒对账。
    /// 整页加载还没结束时改为整页重新加载。
    /// </summary>
    public async Task RefreshAfterStatusChangeAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoading)
        {
            await LoadAsync(cancellationToken);
            return;
        }

        var loadVersion = _loadVersion;
        bool IsCurrent() => loadVersion == _loadVersion && !cancellationToken.IsCancellationRequested;
        try
        {
            var trackingRows = await _tracking.GetAllTrackingAsync();
            var plans = await _actionCenter.GetPlansAsync(cancellationToken: cancellationToken);
            var reminders = await _actionCenter.GetRemindersAsync(
                state: PlanReminderState.Pending,
                cancellationToken: cancellationToken);
            if (!IsCurrent())
                return;

            var statusById = trackingRows.ToDictionary(row => row.AnimeId, row => row.Status);
            var visiblePlans = plans
                .Where(plan => statusById.GetValueOrDefault(plan.AnimeId) != AnimeTrackingStatus.Blocked)
                .ToList();
            var planAnime = await ResolvePlanAnimeAsync(visiblePlans, KnownPlanAnime(), cancellationToken);
            if (!IsCurrent())
                return;

            ApplyPlanEntries(BuildPlanEntries(
                visiblePlans,
                reminders.GroupBy(item => item.AnimeId).ToDictionary(group => group.Key, group => group.Count()),
                planAnime,
                DateOnly.FromDateTime(DateTime.Today)));
            ApplyPersonalStatuses(statusById);
            // 标成看完、弃番或加进补番计划的作品不再算“还没开始看”。
            var activePlanIds = plans.Select(plan => plan.AnimeId).ToHashSet();
            var unstarted = ContinueWatching
                .Where(entry => statusById.GetValueOrDefault(entry.Anime.ID)
                        is AnimeTrackingStatus.Watching or AnimeTrackingStatus.PlanToWatch
                    && !activePlanIds.Contains(entry.Anime.ID))
                .ToList();
            if (unstarted.Count != ContinueWatching.Count)
                ContinueWatching = new ObservableCollection<TodayAnimeEntry>(unstarted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
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
        PersonalBroadcasts = new ObservableCollection<TodayAnimeEntry>(
            PersonalBroadcasts.Where(entry => !blocked.Contains(entry.Anime.ID)));
        ContinueWatching = new ObservableCollection<TodayAnimeEntry>(
            ContinueWatching.Where(
                entry => !blocked.Contains(entry.Anime.ID)));
        RecentActivity = new ObservableCollection<TodayAnimeEntry>(
            RecentActivity.Where(
                entry => !blocked.Contains(entry.Anime.ID)));
        RecentBrowsed = new ObservableCollection<TodayBrowseEntry>(
            RecentBrowsed.Where(
                entry => !blocked.Contains(entry.Anime.ID)));
        // 组标题挂在每组首项上，过滤后要重新分组，否则标题可能丢失或数量不对。
        ApplyPlanEntries(GroupPlanEntries(
            Plans.Where(entry => !blocked.Contains(entry.Plan.AnimeId)),
            DateOnly.FromDateTime(DateTime.Today)));
        _seasonal = _seasonal.Where(anime => !blocked.Contains(anime.ID)).ToList();
        _personalIds.ExceptWith(blocked);
        UpdateBroadcastSummary();
    }

    [RelayCommand]
    public async Task ClearBrowseHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        await _browseHistory.ClearAsync(cancellationToken);
        RecentBrowsed = [];
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
                FormatRelativeTime(record.LastViewed.ToLocalTime(), DateTime.Now)));
        }

        if (IsCurrentLoad(loadVersion, cancellationToken))
            RecentBrowsed = new(entries);
    }

    private async Task LoadPlaybackActivityAsync(
        IReadOnlyDictionary<int, AnimeTrackingStatus> statusById,
        IReadOnlyList<Anime> seasonal,
        IReadOnlySet<int> activePlanIds,
        int loadVersion,
        CancellationToken cancellationToken)
    {
        var progress = await _actionCenter.GetProgressAsync(
            cancellationToken);
        var seasonalById = seasonal.ToDictionary(item => item.ID);
        // 仍有待开始计划的作品已列在补番计划里，“还没开始看”不再重复列出。
        var watchingIds = statusById
            .Where(pair => pair.Value is AnimeTrackingStatus.Watching or AnimeTrackingStatus.PlanToWatch)
            .Where(pair => !progress.ContainsKey(pair.Key))
            .Where(pair => !activePlanIds.Contains(pair.Key))
            .Select(pair => pair.Key)
            .ToList();
        var watchingAnime = await ResolveAnimeAsync(
            watchingIds,
            seasonalById,
            cancellationToken);
        var continueWatching = new ObservableCollection<TodayAnimeEntry>(
            watchingAnime.Select(anime => new TodayAnimeEntry(anime, "尚无播放记录")));
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
                        BuildProgressSubtitle(item))
                    {
                        Trailing = FormatRelativeTime(item.LastWatchedAt.ToLocalTime().DateTime, DateTime.Now),
                        HasProgress = double.IsFinite(item.DurationSeconds) && item.DurationSeconds > 0
                            && double.IsFinite(item.PositionSeconds),
                        ProgressPercent = double.IsFinite(item.DurationSeconds) && item.DurationSeconds > 0
                            && double.IsFinite(item.PositionSeconds)
                            ? Math.Clamp(item.PositionSeconds / item.DurationSeconds * 100, 0, 100) : 0,
                    }));
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

    private async Task EnsureMissingPlansAsync(
        IReadOnlyList<(
            int AnimeId,
            AnimeTrackingStatus Status,
            string UpdatedAt)> tracking,
        IReadOnlyList<Anime> seasonal,
        CancellationToken cancellationToken)
    {
        // 补番状态可能在今日页首次加载后新增；每次加载都只检查尚无计划记录的作品。
        await MissingPlanGate.WaitAsync(cancellationToken);
        try
        {
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
        }
        finally
        {
            MissingPlanGate.Release();
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
                or BangumiApiException
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

    /// <summary>当前计划列表里已经有的作品详情（封面等），刷新时沿用。</summary>
    private Dictionary<int, Anime> KnownPlanAnime()
        => Plans
            .Where(entry => entry.Anime is not null)
            .ToDictionary(entry => entry.Plan.AnimeId, entry => entry.Anime!);

    /// <summary>在已知详情的基础上，只为还没有详情的计划补查（放送日程里有的直接用）。</summary>
    private async Task<Dictionary<int, Anime>> ResolvePlanAnimeAsync(
        IReadOnlyList<AnimePlan> plans,
        IReadOnlyDictionary<int, Anime> known,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, Anime>(known);
        var missing = plans
            .Select(plan => plan.AnimeId)
            .Where(animeId => !result.ContainsKey(animeId))
            .ToList();
        if (missing.Count == 0)
            return result;

        foreach (var anime in await ResolveAnimeAsync(
                     missing,
                     _seasonal.ToDictionary(item => item.ID),
                     cancellationToken))
        {
            result[anime.ID] = anime;
        }

        return result;
    }

    /// <summary>
    /// 更新补番计划列表。计划与顺序都没变时只替换内容有变化的条目（例如补上了封面），
    /// 不整体替换集合：整体替换会重建全部卡片，并让页面收起已展开的卡包。
    /// </summary>
    private void ApplyPlanEntries(IReadOnlyList<TodayPlanEntry> entries)
    {
        if (entries.Count != Plans.Count
            || !entries.Select(entry => entry.Plan.AnimeId)
                .SequenceEqual(Plans.Select(entry => entry.Plan.AnimeId)))
        {
            Plans = new(entries);
            return;
        }

        var changed = false;
        for (var index = 0; index < entries.Count; index++)
        {
            if (Equals(Plans[index], entries[index]))
                continue;

            Plans[index] = entries[index];
            changed = true;
        }

        if (changed)
            UpdatePlanSummary();
    }

    /// <summary>按标记重算今日放送里与你相关的作品和本周放送概况；内容没变时不替换列表。</summary>
    private void ApplyPersonalStatuses(IReadOnlyDictionary<int, AnimeTrackingStatus> statusById)
    {
        var personalIds = statusById
            .Where(pair => pair.Value is
                AnimeTrackingStatus.Watching
                or AnimeTrackingStatus.PlanToWatch
                or AnimeTrackingStatus.Following)
            .Select(pair => pair.Key)
            .ToHashSet();
        var weekdayText = DateTime.Today.ToString(
            "ddd",
            System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));
        // 放送卡片只展示封面、标题、个人状态与放送星期；日历接口不提供简介。
        var broadcasts = AllBroadcasts
            .Where(item => personalIds.Contains(item.ID))
            .Select(item => new TodayAnimeEntry(item, weekdayText)
            {
                Badge = StatusLabels.GetValueOrDefault(statusById[item.ID], string.Empty),
            })
            .ToList();
        if (!broadcasts.SequenceEqual(PersonalBroadcasts))
            PersonalBroadcasts = new ObservableCollection<TodayAnimeEntry>(broadcasts);
        _personalIds = personalIds;
        UpdateBroadcastSummary();
    }

    private void UpdateBroadcastSummary()
    {
        var today = AnimeListPresentation.ToBangumiWeekday(DateTime.Today.DayOfWeek);
        var days = Enumerable.Range(0, 7).Select(offset =>
        {
            var weekday = (today - 1 + offset) % 7 + 1;
            var label = DateTime.Today.AddDays(offset).ToString("ddd", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));
            return new TodayBroadcastDay(label,
                _seasonal.Where(anime => anime.Weekday == weekday && _personalIds.Contains(anime.ID)).ToList())
            {
                IsToday = offset == 0,
            };
        }).ToList();
        BroadcastDays = _scheduleFailed || _personalIds.Count == 0 ? [] : new(days);
        BroadcastMessage = _scheduleFailed ? "放送日程加载失败，请刷新重试。"
            : _personalIds.Count == 0 ? "还没有追番、补番或关注的作品。"
            : PersonalBroadcasts.Count > 0 ? $"今天有 {PersonalBroadcasts.Count} 部个人相关作品安排放送。"
            : "今天暂无与你相关的放送安排。";
        var next = days.Skip(1).Select((day, index) => (Day: day, Offset: index + 1))
            .FirstOrDefault(item => item.Day.Items.Count > 0);
        NextBroadcastText = _scheduleFailed || _personalIds.Count == 0 || PersonalBroadcasts.Count > 0 ? ""
            : next.Day is null ? "当前日历中暂无其他个人相关排期。"
            : $"预计下次：{(next.Offset == 1 ? "明天 " : "")}{next.Day.Label} · {next.Day.Items[0].Title}"
                + (next.Day.Items.Count > 1 ? $" 等 {next.Day.Items.Count} 部" : "");
    }

    internal static string BuildProgressSubtitle(AnimeProgressSnapshot progress)
    {
        static string Clock(double seconds) => double.IsFinite(seconds) && seconds >= 0
            ? $"{(long)(seconds / 60):00}:{(long)(seconds % 60):00}" : "—";
        // 观看时间单独放在行尾，见 FormatRelativeTime。
        return $"第 {progress.CurrentEpisode} 集 · {Clock(progress.PositionSeconds)} / {Clock(progress.DurationSeconds)}";
    }

    /// <summary>当天与前一天显示“今天 / 昨天”，更早的显示日期。</summary>
    internal static string FormatRelativeTime(DateTime localTime, DateTime now)
        => (now.Date - localTime.Date).Days switch
        {
            0 => $"今天 {localTime:HH:mm}",
            1 => $"昨天 {localTime:HH:mm}",
            _ => $"{localTime:M月d日 HH:mm}",
        };

    internal static IReadOnlyList<TodayPlanEntry> BuildPlanEntries(IEnumerable<AnimePlan> plans,
        IReadOnlyDictionary<int, int> reminderCounts, IReadOnlyDictionary<int, Anime> animeById, DateOnly today)
        => GroupPlanEntries(
            plans.Select(plan => new TodayPlanEntry(plan,
                BuildPlanSubtitle(plan, reminderCounts.GetValueOrDefault(plan.AnimeId), today))
            {
                Anime = animeById.GetValueOrDefault(plan.AnimeId),
            }),
            today);

    /// <summary>
    /// 按紧迫度分组并重算组标题与数量。组内按目标日期排列；日期相同（含未排期）时
    /// 保留传入顺序，即服务返回的优先级顺序。过滤掉条目后也用它重新分组。
    /// </summary>
    internal static IReadOnlyList<TodayPlanEntry> GroupPlanEntries(IEnumerable<TodayPlanEntry> entries, DateOnly today)
    {
        int Group(AnimePlan plan) => plan.TargetStartDate is not { } date ? 3
            : date < today ? 0 : date.DayNumber - today.DayNumber <= 7 ? 1 : 2;
        string[] labels = ["已逾期", "近期 · 七天内", "之后", "未排期"];
        return entries.GroupBy(entry => Group(entry.Plan)).OrderBy(group => group.Key).SelectMany(group =>
        {
            var ordered = group.OrderBy(entry => entry.Plan.TargetStartDate ?? DateOnly.MaxValue).ToList();
            return ordered.Select((entry, index) => entry with
            {
                GroupHeader = index == 0 ? $"{labels[group.Key]} · {ordered.Count}" : "",
                IsOverdue = group.Key == 0,
            });
        }).ToList();
    }

    private static string BuildPlanSubtitle(
        AnimePlan plan,
        int reminderCount,
        DateOnly? today = null)
    {
        var reminderText = reminderCount == 0
            ? ""
            : $"{reminderCount} 个提醒";
        if (plan.TargetStartDate is null)
        {
            return reminderText;
        }

        var days = plan.TargetStartDate.Value.DayNumber
            - (today ?? DateOnly.FromDateTime(DateTime.Today)).DayNumber;
        var dateText = days switch
        {
            < 0 => $"已逾期 {-days} 天",
            0 => "计划今天开始",
            <= 7 => $"{days} 天后开始",
            _ => $"目标 {plan.TargetStartDate:yyyy-MM-dd}",
        };
        return reminderCount == 0 ? dateText : $"{dateText} · {reminderText}";
    }

}
