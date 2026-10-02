using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Services;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Text.Json;

namespace AniMeido.Plugin.Base.ViewModels
{
    public enum CalendarSort
    {
        Score,
        AirDate,
        Title,
    }

    /// <summary>放送日历中的一部作品及其本地标记。</summary>
    /// <param name="anime">作品。</param>
    /// <param name="isOther">不是已确认且有星期排期的 TV，归入“其他”一格的作品。</param>
    public sealed partial class CalendarEntry(Anime anime, bool isOther = false) : ObservableObject
    {
        public Anime Anime { get; private set; } = anime;

        public bool IsOther { get; private set; } = isOther;

        /// <summary>所在的格子：周一至周日为 1–7，“其他”为 <see cref="CalendarDay.OtherKey"/>。</summary>
        public int DayKey => IsOther ? CalendarDay.OtherKey : Anime.Weekday ?? 0;

        /// <summary>卡片右上角的标注：周更作品写星期，“其他”里的作品写形态。</summary>
        public string WeekdayText => IsOther
            ? GetFormatBadge(Anime.MediaFormat)
            : AnimeListPresentation.GetWeekdayName(Anime.Weekday);

        /// <summary>按天浏览时只有“其他”需要标形态；星期已由所在格子表明。</summary>
        public string? ShelfBadgeText => IsOther ? WeekdayText : null;

        /// <summary>已经上映或发售，才谈得上追番。没有日期的按未上映处理。</summary>
        public bool IsReleased =>
            Anime.AirDate is { } airDate
            && airDate <= DateOnly.FromDateTime(DateTime.Today);

        /// <summary>未上映的“其他”作品只能关注，上映后才出现“追番”。</summary>
        public bool ShowWatchAction => !IsOther || IsReleased;

        [ObservableProperty]
        private AnimeTrackingStatus _status;

        [ObservableProperty]
        private bool _isSavingStatus;

        /// <summary>最近一次写入开始时的序号，重新读取时据此判断读到的状态是否已过时。</summary>
        internal long StatusWriteStamp { get; set; }

        public bool IsMine => Status is AnimeTrackingStatus.Watching
            or AnimeTrackingStatus.Following;

        /// <summary>补齐形态时保留同一条目及其标记写入，通知卡片更新分组与徽标。</summary>
        internal bool UpdateAnime(Anime value, bool isOther)
        {
            if (Anime == value && IsOther == isOther)
                return false;

            Anime = value;
            IsOther = isOther;
            OnPropertyChanged(nameof(Anime));
            OnPropertyChanged(nameof(IsOther));
            OnPropertyChanged(nameof(DayKey));
            OnPropertyChanged(nameof(WeekdayText));
            OnPropertyChanged(nameof(ShelfBadgeText));
            OnPropertyChanged(nameof(IsReleased));
            OnPropertyChanged(nameof(ShowWatchAction));
            return true;
        }

        private static string GetFormatBadge(AnimeMediaFormat format) => format switch
        {
            AnimeMediaFormat.Movie => "剧场版",
            AnimeMediaFormat.Ova => "OVA",
            AnimeMediaFormat.Ona => "网络",
            AnimeMediaFormat.Television => "TV",
            _ => "其他",
        };
    }

    /// <summary>星期格上的一个小点：追番中或关注中。</summary>
    public sealed record CalendarDot(bool IsFollowing)
    {
        public bool IsWatching => !IsFollowing;
    }

    /// <summary>顶部八格之一：周一至周日的已确认 TV，以及其余作品的“其他”。</summary>
    public sealed partial class CalendarDay(
        int weekday,
        string label,
        bool isToday) : ObservableObject
    {
        /// <summary>“其他”一格的键，接在周日（7）之后。</summary>
        public const int OtherKey = 8;

        public int Weekday { get; } = weekday;

        public bool IsOther => Weekday == OtherKey;

        /// <summary>“其他”未选中时画虚线框；选中后改用与星期格相同的实线描边。</summary>
        public bool ShowOtherOutline => IsOther && !IsSelected;

        public string Label { get; } = label;

        public bool IsToday { get; } = isToday;

        [ObservableProperty]
        private string _countText = "";

        [ObservableProperty]
        private IReadOnlyList<CalendarDot> _dots = [];

        [ObservableProperty]
        private bool _isSelected;

        partial void OnIsSelectedChanged(bool value)
            => OnPropertyChanged(nameof(ShowOtherOutline));
    }

    public partial class CurrentSeasonViewModel : ObservableObject
    {
        /// <summary>标记写入中按钮的不透明度（与卡片上的快捷按钮一致）。</summary>
        public const double SavingActionOpacity = 0.55;

        private readonly IAnimeDataSource _animeDataSource;
        private readonly TrackingService _tracking;
        private IReadOnlyList<Anime> _schedule = [];
        // 本季不在原始放送表里的作品，来自按季查询；放送表中的非 TV 也显示在“其他”。
        private IReadOnlyList<Anime> _others = [];
        private bool _othersLoaded;
        private bool _othersFailed;
        private bool _othersNoSchedule;
        private CancellationTokenSource? _othersCts;
        private bool _supplementaryLoadsSuspended;
        private int _supplementaryLoadGeneration;
        private IReadOnlyList<CalendarEntry> _entries = [];
        private bool _suppressRefresh;
        private int _discoverCapacity;
        private long _statusWriteCount;

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private string? _errorMessage;

        [ObservableProperty]
        private bool _hasData;

        [ObservableProperty]
        private bool _isError;

        [ObservableProperty]
        private string _searchText = "";

        [ObservableProperty]
        private bool _showMineOnly;

        [ObservableProperty]
        private CalendarSort _sort = CalendarSort.Score;

        [ObservableProperty]
        private int _selectedWeekday =
            AnimeListPresentation.ToBangumiWeekday(DateTime.Today.DayOfWeek);

        [ObservableProperty]
        private ObservableCollection<CalendarEntry> _visibleEntries = [];

        [ObservableProperty]
        private string _seasonSummary = "按放送表，上线时间以平台为准";

        [ObservableProperty]
        private string _totalCountText = "";

        [ObservableProperty]
        private string _mineCountText = "";

        [ObservableProperty]
        private string _listTitle = "";

        [ObservableProperty]
        private string _listCaption = "";

        [ObservableProperty]
        private string _emptyText = "";

        public CurrentSeasonViewModel(
            IAnimeDataSource dataSource,
            TrackingService tracking)
        {
            _animeDataSource = dataSource;
            _tracking = tracking;
            var today = AnimeListPresentation.ToBangumiWeekday(
                DateTime.Today.DayOfWeek);
            Days = new ObservableCollection<CalendarDay>(
                Enumerable.Range(1, 7)
                    .Select(weekday => new CalendarDay(
                        weekday,
                        AnimeListPresentation.GetWeekdayName(weekday),
                        weekday == today))
                    .Append(new CalendarDay(CalendarDay.OtherKey, "其他", isToday: false)));
        }

        public ObservableCollection<CalendarDay> Days { get; }

        /// <summary>搜索有内容或只看“我的”时，结果跨越多天，改用网格显示。</summary>
        public bool IsFiltering =>
            ShowMineOnly || !string.IsNullOrWhiteSpace(SearchText);

        public bool HasNoVisibleEntries => VisibleEntries.Count == 0;

        /// <summary>“其他”固定已标记优先、组内按上映日期排列，排序选项只在其余情况下生效。</summary>
        public bool IsSortApplicable =>
            IsFiltering || SelectedWeekday != CalendarDay.OtherKey;

        partial void OnSearchTextChanged(string value)
        {
            OnPropertyChanged(nameof(IsFiltering));
            OnPropertyChanged(nameof(IsSortApplicable));
            if (!_suppressRefresh)
                Refresh(rebuildList: true);
        }

        partial void OnShowMineOnlyChanged(bool value)
        {
            OnPropertyChanged(nameof(IsFiltering));
            OnPropertyChanged(nameof(IsSortApplicable));
            if (!_suppressRefresh)
                Refresh(rebuildList: true);
        }

        partial void OnSortChanged(CalendarSort value) => Refresh(rebuildList: true);

        partial void OnSelectedWeekdayChanged(int value)
            => OnPropertyChanged(nameof(IsSortApplicable));

        partial void OnVisibleEntriesChanged(ObservableCollection<CalendarEntry> value)
            => OnPropertyChanged(nameof(HasNoVisibleEntries));

        /// <summary>
        /// 切换到某一天。搜索或只看“我的”时点星期格，会清空搜索、回到全部，跳到那一天。
        /// </summary>
        public void SelectDay(int weekday)
        {
            _suppressRefresh = true;
            try
            {
                SelectedWeekday = weekday;
                SearchText = "";
                ShowMineOnly = false;
            }
            finally
            {
                _suppressRefresh = false;
            }

            Refresh(rebuildList: true);

            // “其他”加载失败时，点这一格就是重试。
            if (weekday == CalendarDay.OtherKey && _othersFailed)
            {
                RetryOthersCommand.Execute(null);
            }
        }

        /// <summary>按剩余高度能完整放下的行数更新番剧时光机的条数。</summary>
        public void SetDiscoverCapacity(int capacity)
        {
            capacity = Math.Max(0, capacity);
            if (capacity == _discoverCapacity)
            {
                return;
            }

            _discoverCapacity = capacity;
            RefreshTimeMachine();
        }

        /// <summary>与详情页一致：已是该状态则取消，否则设为该状态。</summary>
        public async Task ToggleStatusAsync(int animeId, AnimeTrackingStatus status)
        {
            var entry = _entries.FirstOrDefault(item => item.Anime.ID == animeId);
            if (entry is null || entry.IsSavingStatus)
            {
                return;
            }

            entry.IsSavingStatus = true;
            entry.StatusWriteStamp = ++_statusWriteCount;
            try
            {
                if (entry.Status == status)
                {
                    await _tracking.RemoveStatusAsync(animeId);
                    entry.Status = AnimeTrackingStatus.None;
                }
                else
                {
                    await _tracking.SetStatusAsync(animeId, status);
                    entry.Status = status;
                }

                // “其他”的标记会改变优先顺序；其余普通视图仍保留既有列表位置。
                Refresh(
                    rebuildList: ShowMineOnly || (!IsFiltering && SelectedWeekday == CalendarDay.OtherKey),
                    keepUnchangedList: true);
            }
            finally
            {
                entry.IsSavingStatus = false;
            }
        }

        /// <summary>
        /// 重新读取本地标记（例如拖放标记或从详情页返回后）。新屏蔽的作品会被移除。
        /// </summary>
        public async Task ReloadStatusesAsync()
        {
            if (_schedule.Count == 0)
            {
                return;
            }

            var supplementaryGeneration = _supplementaryLoadGeneration;
            var snapshot = BeginStatusRead();
            var statuses = await TryReadStatusesAsync();
            if (statuses is null
                || _supplementaryLoadsSuspended
                || supplementaryGeneration != _supplementaryLoadGeneration)
            {
                // 重新读取失败时保留屏幕上的既有状态，避免把所有标记误显示为“未设置”。
                return;
            }

            ApplyStatuses(statuses, snapshot);
            await EnsureDailyPickAsync(supplementaryGeneration);
        }

        /// <summary>读取开始时的写入序号，以及当时还没写完的作品。</summary>
        internal sealed record StatusReadSnapshot(long WriteStamp, IReadOnlySet<int> SavingIds);

        /// <summary>在读取本地标记之前调用，用来判断读到的结果对哪些作品已经过时。</summary>
        internal StatusReadSnapshot BeginStatusRead()
            => new(
                _statusWriteCount,
                _entries.Where(entry => entry.IsSavingStatus).Select(entry => entry.Anime.ID)
                    .Concat(_timeMachineEntries
                        .Where(entry => entry.IsSavingStatus)
                        .Select(entry => entry.Anime.ID))
                    .ToHashSet());

        /// <summary>
        /// 读到的标记是否可以覆盖页面上的状态。读取开始时还没写完、读取期间正在写、
        /// 或读取开始之后才发起的写入，都可能比读到的结果新，保留页面上的状态。
        /// </summary>
        internal static bool CanApplyReadStatus(
            int animeId,
            bool isSaving,
            long writeStamp,
            StatusReadSnapshot snapshot)
            => !isSaving
                && !snapshot.SavingIds.Contains(animeId)
                && writeStamp <= snapshot.WriteStamp;

        /// <summary>把读到的标记合并进日历与时光机；新屏蔽的作品被移除，被解除屏蔽的重新出现。</summary>
        internal void ApplyStatuses(
            IReadOnlyDictionary<int, AnimeTrackingStatus> statuses,
            StatusReadSnapshot snapshot)
        {
            ApplyTimeMachineStatuses(statuses, snapshot);

            var previousById = _entries.ToDictionary(entry => entry.Anime.ID);
            var candidates = BuildCandidates(_schedule, _others).ToList();
            var merged = new List<CalendarEntry>(candidates.Count);
            var presentationChanged = false;
            foreach (var candidate in candidates)
            {
                var status = statuses.GetValueOrDefault(candidate.Anime.ID);
                if (previousById.TryGetValue(candidate.Anime.ID, out var previous))
                {
                    var canApply = CanApplyReadStatus(
                        previous.Anime.ID,
                        previous.IsSavingStatus,
                        previous.StatusWriteStamp,
                        snapshot);
                    if (canApply && status == AnimeTrackingStatus.Blocked)
                        continue;

                    if (canApply)
                    {
                        previous.Status = status;
                    }

                    presentationChanged |= previous.UpdateAnime(candidate.Anime, candidate.IsOther);
                    merged.Add(previous);
                }
                else if (status != AnimeTrackingStatus.Blocked)
                {
                    merged.Add(new CalendarEntry(candidate.Anime, candidate.IsOther)
                    {
                        Status = status,
                    });
                }
            }

            var membershipChanged = !_entries.Select(entry => entry.Anime.ID)
                .SequenceEqual(merged.Select(entry => entry.Anime.ID));
            _entries = merged;
            HasData = _entries.Count > 0;
            // 例如“其他”合并进来时，当前这一天的列表没变，不重置滚动位置。
            Refresh(
                rebuildList: membershipChanged || presentationChanged || ShowMineOnly
                    || (!IsFiltering && SelectedWeekday == CalendarDay.OtherKey),
                keepUnchangedList: true);
        }

        [RelayCommand]
        private void RetryLoad()
        {
            LoadSeasonalAnimeCommand.Execute(null);
        }

        /// <summary>
        /// 加载当季放送日程与本地标记
        /// </summary>
        [RelayCommand]
        private async Task LoadSeasonalAnimeAsync(CancellationToken ct = default)
        {
            var supplementaryGeneration = _supplementaryLoadGeneration;
            IsLoading = true;
            IsError = false;
            ErrorMessage = null;
            HasData = false;
            CancelOthersLoad();
            _others = [];
            _othersLoaded = false;
            _othersFailed = false;
            _othersNoSchedule = false;
            try
            {
                // 番剧时光机独立加载，失败或较慢都不影响放送日历本身。
                PendingTimeMachineLoad = LoadTimeMachineAsync();
                var scheduleTask = _animeDataSource
                    .GetCurrentBroadcastScheduleAsync(ct);
                var statuses = await TryReadStatusesAsync()
                    ?? new Dictionary<int, AnimeTrackingStatus>();
                var schedule = await scheduleTask;
                ct.ThrowIfCancellationRequested();

                // 已确认 TV 先显示在星期格，其余暂归“其他”；随后用季度目录补齐形态。
                _schedule = schedule.DistinctBy(anime => anime.ID).ToList();
                _entries = BuildEntries(_schedule, statuses);
                HasData = _entries.Count > 0;
                Refresh(rebuildList: true);
                PendingOthersLoad = LoadOthersAsync();
                await EnsureDailyPickAsync(supplementaryGeneration);
            }
            catch (HttpRequestException ex)
            {
                ErrorMessage = $"网络请求失败：{ex.Message}";
                IsError = true;
            }
            catch (BangumiApiException ex)
            {
                ErrorMessage = $"数据源请求失败：{ex.Message}";
                IsError = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 用户操作引起的取消，静默忽略
                return;
            }
            catch (TaskCanceledException)
            {
                // HTTP 超时或其他网络层取消，作为错误处理
                ErrorMessage = "网络请求超时，请检查网络后重试";
                IsError = true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException)
            {
                ErrorMessage = $"数据解析失败：{ex.Message}";
                IsError = true;
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>“其他”这一次加载，测试用来等待它完成。</summary>
        internal Task PendingOthersLoad { get; private set; } = Task.CompletedTask;

        /// <summary>“其他”加载失败后重试。</summary>
        [RelayCommand]
        private Task RetryOthersAsync() => PendingOthersLoad = LoadOthersAsync();

        /// <summary>
        /// 按季查询补齐放送表的形态：只有已确认 TV 留在星期格，其余及目录补充作品归入“其他”。
        /// 与现有条目合并，保留标记与列表位置，不修改公共放送表或“今天”的数据。
        /// 离开页面时被取消，回到页面后由 <see cref="ResumeSupplementaryLoads"/> 重新开始。
        /// </summary>
        private async Task LoadOthersAsync()
        {
            var supplementaryGeneration = _supplementaryLoadGeneration;
            CancelOthersLoad();
            if (_schedule.Count == 0)
            {
                _othersNoSchedule = true;
                Refresh(rebuildList: SelectedWeekday == CalendarDay.OtherKey, keepUnchangedList: true);
                return;
            }

            var cts = new CancellationTokenSource();
            _othersCts = cts;
            _othersFailed = false;
            Refresh(rebuildList: false);
            try
            {
                var season = await TryLoadSeasonAnimeAsync(cts.Token);
                if (cts.IsCancellationRequested)
                    return;

                if (season is null)
                {
                    _othersFailed = true;
                    return;
                }

                _schedule = ResolveScheduleFormats(_schedule, season);
                _others = ExtractOthers(season, _schedule);
                _othersLoaded = true;
                // 读取标记失败时沿用页面上的标记，新出现的作品按未标记显示。
                var snapshot = BeginStatusRead();
                var statuses = await TryReadStatusesAsync()
                    ?? _entries.ToDictionary(entry => entry.Anime.ID, entry => entry.Status);
                if (cts.IsCancellationRequested)
                {
                    // 已拿到作品，只差合并；回到页面时重新读取标记会把它们带上。
                    return;
                }

                ApplyStatuses(statuses, snapshot);
                await EnsureDailyPickAsync(supplementaryGeneration);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
            finally
            {
                if (ReferenceEquals(_othersCts, cts))
                {
                    _othersCts = null;
                    cts.Dispose();
                    Refresh(rebuildList: SelectedWeekday == CalendarDay.OtherKey, keepUnchangedList: true);
                }
            }
        }

        /// <summary>取消正在进行的“其他”查询；还没拿到结果的，回到页面时重新开始。</summary>
        private void CancelOthersLoad()
        {
            if (_othersCts is not { } cts)
                return;

            _othersCts = null;
            cts.Cancel();
            cts.Dispose();
        }

        /// <summary>“其他”正在加载或等待重新开始（还没有结果，也没有失败）。</summary>
        private bool IsOthersPending => !_othersNoSchedule && !_othersLoaded && !_othersFailed;

        /// <summary>
        /// 页面离开时调用：取消“其他”、番剧时光机和今日一抽详情的请求，
        /// 结果不再写回已离开的页面。主加载由页面自己取消。
        /// </summary>
        public void CancelSupplementaryLoads()
        {
            _supplementaryLoadsSuspended = true;
            _supplementaryLoadGeneration++;
            CancelOthersLoad();
            CancelTimeMachineLoad();
            CancelDailyPickDetails();
        }

        /// <summary>
        /// 页面回到前台时调用（例如从详情页返回，页面实例被复用）：
        /// 只重新开始被取消、还没有结果的请求；失败的不自动重试，等用户点重试。
        /// 正在进行的请求不重复发起。
        /// </summary>
        public void ResumeSupplementaryLoads()
        {
            _supplementaryLoadsSuspended = false;

            if (HasData && IsOthersPending && _othersCts is null)
                PendingOthersLoad = LoadOthersAsync();

            if (_timeMachineLoadedTarget is null
                && !IsTimeMachineFailed
                && _timeMachineCts is null)
            {
                PendingTimeMachineLoad = LoadTimeMachineAsync();
            }

            if (DailyPick is { } pick
                && !ReferenceEquals(_dailyPickDetailsFor, pick)
                && _dailyPickDetailCts is null)
            {
                _ = LoadDailyPickDetailsAsync(pick);
            }
        }

        /// <summary>
        /// 按季查询本季全部作品，用于补齐放送形态和目录。失败时保留现有条目，未知形态仍归“其他”。
        /// </summary>
        private async Task<IReadOnlyList<Anime>?> TryLoadSeasonAnimeAsync(CancellationToken ct)
        {
            try
            {
                var (year, season) = SeasonHelper.GetCurrentSeason();
                return await _animeDataSource.GetAnimeBySeasonAsync(year, season, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException
                or BangumiApiException
                or TaskCanceledException
                or InvalidOperationException
                or JsonException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CurrentSeasonViewModel] TryLoadSeasonAnimeAsync failed: {ex.Message}");
                return null;
            }
        }

        private async Task<IReadOnlyDictionary<int, AnimeTrackingStatus>?> TryReadStatusesAsync()
        {
            try
            {
                return (await _tracking.GetAllTrackingAsync())
                    .ToDictionary(row => row.AnimeId, row => row.Status);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                // 初次加载可继续显示日程；后续刷新由调用方保留既有状态。
                System.Diagnostics.Debug.WriteLine(
                    $"[CurrentSeasonViewModel] TryReadStatusesAsync failed: {ex.Message}");
                return null;
            }
        }

        /// <param name="rebuildList">重建当前显示的列表（会回到开头）。</param>
        /// <param name="keepUnchangedList">重建出的列表与现有的完全相同时保留现有列表。</param>
        private void Refresh(bool rebuildList, bool keepUnchangedList = false)
        {
            var mineCount = _entries.Count(entry => entry.IsMine);
            TotalCountText = _entries.Count.ToString(CultureInfo.InvariantCulture);
            MineCountText = mineCount.ToString(CultureInfo.InvariantCulture);
            var (year, season) = SeasonHelper.GetCurrentSeason();
            SeasonSummary =
                $"{year} 年{GetSeasonName(season)} · 共 {_entries.Count} 部 · 上线时间以平台为准";

            var filtering = IsFiltering;

            var query = SearchText.Trim();
            var matches = _entries
                .Where(entry => !ShowMineOnly || entry.IsMine)
                .Where(entry => query.Length == 0
                    || entry.Anime.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var day in Days)
            {
                var total = _entries.Count(entry => entry.DayKey == day.Weekday);
                var matched = matches.Count(entry => entry.DayKey == day.Weekday);
                day.CountText = !day.IsOther
                    ? BuildDayCountText(matched, total, filtering)
                    : _othersNoSchedule || _othersFailed
                        ? "–"
                        : IsOthersPending
                            ? "…"
                            : BuildDayCountText(matched, total, filtering);
                day.Dots = filtering
                    ? []
                    : _entries
                        .Where(entry => entry.DayKey == day.Weekday && entry.IsMine)
                        .Select(entry => new CalendarDot(
                            entry.Status == AnimeTrackingStatus.Following))
                        .ToList();
                day.IsSelected = !filtering && SelectedWeekday == day.Weekday;
            }

            IReadOnlyList<CalendarEntry> list;
            if (filtering)
            {
                list = matches;
                ListTitle = $"找到 {list.Count} 部";
                ListCaption = "点击上方某天，清空搜索并跳到那一天";
                EmptyText = "没有符合条件的番剧";
            }
            else if (SelectedWeekday == CalendarDay.OtherKey)
            {
                list = OrderOthers(_entries.Where(entry => entry.IsOther));
                ListTitle = $"其他 · {list.Count} 部";
                var otherMine = list.Count(entry => entry.IsMine);
                ListCaption = _othersFailed
                    ? "加载失败，点击上方“其他”重试"
                    : otherMine > 0
                        ? $"你标记的 {otherMine} 部排在前面 · 按上映日期排列"
                        : "网络动画、剧场版、OVA 等 · 按上映日期排列";
                EmptyText = _othersNoSchedule
                    ? "本季放送表还没有数据，暂时无法整理其他作品"
                    : _othersFailed
                        ? "本季作品形态与补充目录没有加载出来"
                        : IsOthersPending
                            ? "正在确认作品形态并加载本季其他作品…"
                            : "本季没有其他作品";
            }
            else
            {
                list = _entries
                    .Where(entry => entry.DayKey == SelectedWeekday)
                    .ToList();
                var dayMine = list.Count(entry => entry.IsMine);
                ListTitle =
                    $"{AnimeListPresentation.GetWeekdayName(SelectedWeekday)} · {list.Count} 部";
                ListCaption = dayMine > 0 ? $"你标记的 {dayMine} 部排在前面" : "";
                EmptyText = IsOthersPending && _schedule.Any(anime => anime.MediaFormat == AnimeMediaFormat.Unknown)
                    ? "正在确认本季 TV 动画的放送排期…"
                    : "这一天没有已确认的 TV 动画排期";
            }

            if (rebuildList)
            {
                var ordered = !filtering && SelectedWeekday == CalendarDay.OtherKey
                    ? list
                    : Order(list, Sort);
                if (!keepUnchangedList || !ordered.SequenceEqual(VisibleEntries))
                    VisibleEntries = new ObservableCollection<CalendarEntry>(ordered);
            }
        }

        internal static IReadOnlyList<CalendarEntry> BuildEntries(
            IEnumerable<Anime> schedule,
            IReadOnlyDictionary<int, AnimeTrackingStatus> statuses)
            => BuildEntries(schedule, [], statuses);

        /// <summary>只有放送表中已确认且有星期排期的 TV 进入星期格，其余归入“其他”；屏蔽的作品不显示。</summary>
        internal static IReadOnlyList<CalendarEntry> BuildEntries(
            IEnumerable<Anime> schedule,
            IEnumerable<Anime> others,
            IReadOnlyDictionary<int, AnimeTrackingStatus> statuses)
            => BuildCandidates(schedule, others)
                .Where(item => statuses.GetValueOrDefault(item.Anime.ID)
                    != AnimeTrackingStatus.Blocked)
                .Select(item => new CalendarEntry(item.Anime, item.IsOther)
                {
                    Status = statuses.GetValueOrDefault(item.Anime.ID),
                })
                .ToList();

        private static IEnumerable<(Anime Anime, bool IsOther)> BuildCandidates(
            IEnumerable<Anime> schedule,
            IEnumerable<Anime> others)
            => schedule
                .Select(anime => (Anime: anime, IsOther: anime.MediaFormat != AnimeMediaFormat.Television
                    || anime.Weekday is not (>= 1 and <= 7)))
                .Concat(others.Select(anime => (Anime: anime, IsOther: true)))
                .DistinctBy(item => item.Anime.ID);

        /// <summary>只补齐形态，保留日历的日期、星期及评分；缺失或未知的目录信息不猜成 TV。</summary>
        internal static IReadOnlyList<Anime> ResolveScheduleFormats(
            IEnumerable<Anime> schedule,
            IEnumerable<Anime> season)
        {
            var formats = season.DistinctBy(anime => anime.ID)
                .ToDictionary(anime => anime.ID, anime => anime.MediaFormat);
            return schedule.DistinctBy(anime => anime.ID)
                .Select(anime => formats.TryGetValue(anime.ID, out var format)
                    && format != AnimeMediaFormat.Unknown && format != anime.MediaFormat
                        ? anime with { MediaFormat = format }
                        : anime)
                .ToList();
        }

        /// <summary>按季查询的结果去掉原始放送表里已有的作品，避免重复；表内的非 TV 由 BuildCandidates 归入“其他”。</summary>
        internal static IReadOnlyList<Anime> ExtractOthers(
            IEnumerable<Anime> season,
            IEnumerable<Anime> schedule)
        {
            var scheduled = schedule.Select(anime => anime.ID).ToHashSet();
            return season
                .Where(anime => !scheduled.Contains(anime.ID))
                .DistinctBy(anime => anime.ID)
                .ToList();
        }

        /// <summary>“其他”已标记的（追番中、关注中）优先；各组按上映或发售日期从早到晚排列，没有日期的放在组末。</summary>
        internal static IReadOnlyList<CalendarEntry> OrderOthers(
            IEnumerable<CalendarEntry> entries)
            => entries
                .OrderByDescending(entry => entry.IsMine)
                .ThenBy(entry => entry.Anime.AirDate ?? DateOnly.MaxValue)
                .ThenBy(entry => entry.Anime.Title, TitleSortComparer.Instance)
                .ThenBy(entry => entry.Anime.ID)
                .ToList();

        /// <summary>你标记的（追番中、关注中）排在前面，其余按所选方式排序。</summary>
        internal static IReadOnlyList<CalendarEntry> Order(
            IEnumerable<CalendarEntry> entries,
            CalendarSort sort)
        {
            var mineFirst = entries.OrderByDescending(entry => entry.IsMine);
            return (sort switch
            {
                CalendarSort.AirDate => mineFirst
                    .ThenBy(entry => entry.Anime.AirDate ?? DateOnly.MaxValue),
                CalendarSort.Title => mineFirst
                    .ThenBy(entry => entry.Anime.Title, TitleSortComparer.Instance)
                    .ThenBy(entry => entry.Anime.ID),
                _ => mineFirst
                    .ThenByDescending(entry => entry.Anime.Score ?? double.MinValue),
            }).ToList();
        }

        internal static string BuildDayCountText(int matched, int total, bool filtering)
            => filtering ? $"{matched} / {total} 部" : $"{total} 部";

        private static string GetSeasonName(Season season) => season switch
        {
            Season.Winter => "冬季",
            Season.Spring => "春季",
            Season.Summer => "夏季",
            _ => "秋季",
        };
    }
}
