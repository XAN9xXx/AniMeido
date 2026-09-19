using System.Collections.ObjectModel;
using System.Globalization;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AniMeido.Plugin.Base.ViewModels
{
    /// <summary>番剧时光机中的一行：往年同一季的作品与你的标记。</summary>
    public sealed partial class TimeMachineEntry(int rank, Anime anime) : ObservableObject
    {
        private static readonly IReadOnlyDictionary<AnimeTrackingStatus, string> StatusLabels =
            TrackingActionDescriptor.CreateDefaults()
                .ToDictionary(action => action.Status, action => action.ActiveLabel);

        public int Rank { get; } = rank;

        public Anime Anime { get; } = anime;

        public string RankText => Rank.ToString(CultureInfo.InvariantCulture);

        public string ScoreText => Anime.Score is { } score
            ? score.ToString("F1", CultureInfo.InvariantCulture)
            : "";

        public string Meta => Anime.MediaFormat == AnimeMediaFormat.Unknown
            ? ""
            : AnimeReleaseClassifier.GetMediaFormatText(Anime.MediaFormat);

        [ObservableProperty]
        private AnimeTrackingStatus _status;

        [ObservableProperty]
        private bool _isSavingStatus;

        /// <summary>最近一次写入开始时的序号，重新读取时据此判断读到的状态是否已过时。</summary>
        internal long StatusWriteStamp { get; set; }

        /// <summary>写入中按钮变淡；按钮仍接住点击，不禁用，避免焦点被挤走。</summary>
        public double ActionOpacity => IsSavingStatus
            ? CurrentSeasonViewModel.SavingActionOpacity
            : 1;

        partial void OnIsSavingStatusChanged(bool value) => OnPropertyChanged(nameof(ActionOpacity));

        /// <summary>已标记时显示的状态，例如“已看完”“补番中”。</summary>
        public string StatusText => StatusLabels.GetValueOrDefault(Status, "");

        public bool HasStatus => StatusText.Length > 0;

        public string PlanLabel => Status == AnimeTrackingStatus.PlanToWatch ? "补番中 ✓" : "补番";

        public string FollowLabel => Status == AnimeTrackingStatus.Following ? "关注中 ✓" : "关注";

        partial void OnStatusChanged(AnimeTrackingStatus value)
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(HasStatus));
            OnPropertyChanged(nameof(PlanLabel));
            OnPropertyChanged(nameof(FollowLabel));
        }
    }

    /// <summary>
    /// 放送日历下方的“番剧时光机”：从往年同一季随机翻出几部，已标记的保留并显示状态。
    /// 默认十年前，可切换五年前、二十年前。
    /// 每个年份在一次运行中只抽一次（第一次看到它时），之后切换年份、切换页面都沿用；
    /// 重启应用才重新抽。显示过的作品记在本地，重启后优先抽没显示过的，
    /// 一季的作品都显示过一遍后从头再来。
    /// </summary>
    public partial class CurrentSeasonViewModel
    {
        // 以下状态跨页面实例共享（每次从导航进入放送日历都会新建页面），重启后清空。
        private static readonly object TimeMachineSessionGate = new();
        // 每一季本次运行中抽到的顺序（作品 ID）。
        private static readonly Dictionary<PastSeasonTarget, IReadOnlyList<int>> TimeMachineDraws = [];
        private static int s_timeMachineYearsAgo = 10;
        // 写入“显示过的作品”时逐个进行，避免两次读改写互相覆盖。
        private static readonly SemaphoreSlim TimeMachineShownGate = new(1, 1);

        // 这一季全部候选，按抽到的顺序排列；显示时取前若干部。
        private IReadOnlyList<TimeMachineEntry> _timeMachineEntries = [];
        private PastSeasonTarget? _timeMachineLoadedTarget;
        private CancellationTokenSource? _timeMachineCts;
        // 这个页面已经记下的“显示过的作品”，避免调整窗口大小时反复写入。
        private readonly HashSet<(PastSeasonTarget Target, int AnimeId)> _recordedShown = [];

        public IReadOnlyList<int> TimeMachineYearOptions { get; } = [5, 10, 20];

        [ObservableProperty]
        private int _timeMachineYearsAgo = SessionYearsAgo();

        [ObservableProperty]
        private ObservableCollection<TimeMachineEntry> _timeMachinePicks = [];

        [ObservableProperty]
        private bool _isTimeMachineLoading;

        [ObservableProperty]
        private bool _isTimeMachineFailed;

        /// <summary>时光机指向的那一季：今年的同一季往前推若干年。</summary>
        public PastSeasonTarget TimeMachineTarget
        {
            get
            {
                var (year, season) = SeasonHelper.GetCurrentSeason();
                return new PastSeasonTarget(year - TimeMachineYearsAgo, season);
            }
        }

        public string TimeMachineCaption
        {
            get
            {
                var target = TimeMachineTarget;
                return $"{target.Year} 年{GetSeasonName(target.Season)}";
            }
        }

        public bool HasTimeMachinePicks => TimeMachinePicks.Count > 0;

        /// <summary>加载中、失败或这一季没有可显示的作品时，列表位置显示的说明。</summary>
        public string TimeMachineStatusText => IsTimeMachineLoading
            ? $"正在翻出 {TimeMachineCaption} 的番剧…"
            : IsTimeMachineFailed
                ? "没有加载出来，点“重试”再试一次"
                : HasTimeMachinePicks ? "" : "这一季没有带评分的作品";

        public bool HasTimeMachineStatusText => TimeMachineStatusText.Length > 0;

        /// <summary>切换年份触发的那次加载，测试用来等待它完成。</summary>
        internal Task PendingTimeMachineLoad { get; private set; } = Task.CompletedTask;

        /// <summary>最近一次记下“显示过的作品”，测试用来等待它完成。</summary>
        internal Task PendingTimeMachineShownSave { get; private set; } = Task.CompletedTask;

        /// <summary>测试用：清空本次运行中保留的时光机状态。</summary>
        internal static void ResetTimeMachineSession()
        {
            lock (TimeMachineSessionGate)
            {
                TimeMachineDraws.Clear();
                s_timeMachineYearsAgo = 10;
            }
        }

        private static int SessionYearsAgo()
        {
            lock (TimeMachineSessionGate)
            {
                return s_timeMachineYearsAgo;
            }
        }

        /// <summary>切换年份：记住选择；这个年份本次运行中抽过就沿用，没抽过才抽。</summary>
        partial void OnTimeMachineYearsAgoChanged(int value)
        {
            lock (TimeMachineSessionGate)
            {
                s_timeMachineYearsAgo = value;
            }

            OnPropertyChanged(nameof(TimeMachineTarget));
            OnPropertyChanged(nameof(TimeMachineCaption));
            PendingTimeMachineLoad = LoadTimeMachineAsync();
        }

        partial void OnTimeMachinePicksChanged(ObservableCollection<TimeMachineEntry> value)
        {
            OnPropertyChanged(nameof(HasTimeMachinePicks));
            RaiseTimeMachineStatusText();
        }

        partial void OnIsTimeMachineLoadingChanged(bool value) => RaiseTimeMachineStatusText();

        partial void OnIsTimeMachineFailedChanged(bool value) => RaiseTimeMachineStatusText();

        private void RaiseTimeMachineStatusText()
        {
            OnPropertyChanged(nameof(TimeMachineStatusText));
            OnPropertyChanged(nameof(HasTimeMachineStatusText));
        }

        [RelayCommand]
        private Task RetryTimeMachineAsync() => LoadTimeMachineAsync();

        /// <summary>
        /// 显示时光机指向的那一季：本次运行中这一季抽过就沿用那一批；
        /// 没抽过才抽，以前运行中显示过的排到后面。
        /// 切换年份时取消上一次请求，旧结果不会覆盖新选择。
        /// 这只是页面下方的补充内容，任何失败都只显示重试，不影响放送日历本身。
        /// </summary>
        public async Task LoadTimeMachineAsync()
        {
            CancelTimeMachineLoad();
            var cts = new CancellationTokenSource();
            _timeMachineCts = cts;

            var target = TimeMachineTarget;
            IsTimeMachineFailed = false;
            IsTimeMachineLoading = true;
            _timeMachineEntries = [];
            _timeMachineLoadedTarget = null;
            RefreshTimeMachine();
            try
            {
                // 按季查询有缓存，切换页面再回来时通常不会真的发网络请求。
                var season = await _animeDataSource.GetAnimeBySeasonAsync(
                    target.Year,
                    target.Season,
                    cts.Token);
                var statuses = await TryReadStatusesAsync()
                    ?? new Dictionary<int, AnimeTrackingStatus>();
                if (cts.IsCancellationRequested)
                    return;

                var pool = RankTimeMachine(season, statuses);
                IReadOnlyList<int>? stored;
                lock (TimeMachineSessionGate)
                {
                    stored = TimeMachineDraws.GetValueOrDefault(target);
                }

                if (stored is not null)
                {
                    // 本次运行中抽过：沿用那一批。
                    _timeMachineEntries = KeepStoredOrder(pool, stored, Random.Shared);
                }
                else
                {
                    // 本次运行第一次抽这一季：以前显示过的排到后面；都显示过一遍就从头再来。
                    var shown = await TryLoadTimeMachineShownAsync(target);
                    if (cts.IsCancellationRequested)
                        return;

                    _timeMachineEntries = OrderForDisplay(pool, shown, Random.Shared, out var startOver);
                    if (startOver)
                        await RecordTimeMachineShownAsync(target, [], replace: true);
                }

                lock (TimeMachineSessionGate)
                {
                    TimeMachineDraws[target] = _timeMachineEntries
                        .Select(entry => entry.Anime.ID)
                        .ToList();
                }

                _timeMachineLoadedTarget = target;
                RefreshTimeMachine();
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // 时光机是补充内容，失败只显示重试，不打断页面
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CurrentSeasonViewModel] LoadTimeMachineAsync failed: {ex.Message}");
                if (ReferenceEquals(_timeMachineCts, cts))
                    IsTimeMachineFailed = true;
            }
#pragma warning restore CA1031
            finally
            {
                if (ReferenceEquals(_timeMachineCts, cts))
                {
                    _timeMachineCts = null;
                    cts.Dispose();
                    IsTimeMachineLoading = false;
                }
            }
        }

        /// <summary>取消正在进行的时光机请求；还没有结果的，回到页面时重新开始。</summary>
        private void CancelTimeMachineLoad()
        {
            if (_timeMachineCts is not { } cts)
                return;

            _timeMachineCts = null;
            cts.Cancel();
            cts.Dispose();
            IsTimeMachineLoading = false;
        }

        /// <summary>与卡片一致：已是该状态则取消，否则设为该状态。</summary>
        public async Task ToggleTimeMachineStatusAsync(int animeId, AnimeTrackingStatus status)
        {
            var entry = _timeMachineEntries.FirstOrDefault(item => item.Anime.ID == animeId);
            if (entry is null || entry.IsSavingStatus)
                return;

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
            }
            finally
            {
                entry.IsSavingStatus = false;
            }
        }

        /// <summary>重新读取标记后同步到时光机；新屏蔽的作品从列表移除。</summary>
        private void ApplyTimeMachineStatuses(
            IReadOnlyDictionary<int, AnimeTrackingStatus> statuses,
            StatusReadSnapshot snapshot)
        {
            var blockedRemoved = false;
            var merged = new List<TimeMachineEntry>(_timeMachineEntries.Count);
            foreach (var entry in _timeMachineEntries)
            {
                var status = statuses.GetValueOrDefault(entry.Anime.ID);
                var canApply = CanApplyReadStatus(
                    entry.Anime.ID,
                    entry.IsSavingStatus,
                    entry.StatusWriteStamp,
                    snapshot);
                if (canApply && status == AnimeTrackingStatus.Blocked)
                {
                    blockedRemoved = true;
                    continue;
                }

                if (canApply)
                {
                    entry.Status = status;
                }

                merged.Add(entry);
            }

            if (!blockedRemoved)
                return;

            _timeMachineEntries = merged;
            RefreshTimeMachine();
        }

        /// <summary>按抽到的顺序取前若干部，不按评分重排；显示出来的记为“显示过”。</summary>
        private void RefreshTimeMachine()
        {
            TimeMachinePicks = new ObservableCollection<TimeMachineEntry>(
                _timeMachineEntries.Take(_discoverCapacity));
            RememberTimeMachineShown();
        }

        private void RememberTimeMachineShown()
        {
            if (_timeMachineLoadedTarget is not { } target)
                return;

            var fresh = TimeMachinePicks
                .Select(entry => entry.Anime.ID)
                .Where(id => _recordedShown.Add((target, id)))
                .ToList();
            if (fresh.Count > 0)
                PendingTimeMachineShownSave = RecordTimeMachineShownAsync(target, fresh, replace: false);
        }

        private async Task<IReadOnlySet<int>> TryLoadTimeMachineShownAsync(PastSeasonTarget target)
        {
            try
            {
                var shown = await _tracking.LoadTimeMachineShownAsync();
                return shown.TryGetValue(target, out var ids) ? ids.ToHashSet() : [];
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CurrentSeasonViewModel] LoadTimeMachineShownAsync failed: {ex.Message}");
                return new HashSet<int>();
            }
        }

        /// <summary>
        /// 把作品记为这一季“显示过”（<paramref name="replace"/> 为 true 时先清空这一季的记录）。
        /// 只保留当前可选年份对应的几季，换季后旧记录自然去掉。写入失败只影响下次能否避开重复。
        /// </summary>
        private async Task RecordTimeMachineShownAsync(
            PastSeasonTarget target,
            IReadOnlyCollection<int> animeIds,
            bool replace)
        {
            await TimeMachineShownGate.WaitAsync();
            try
            {
                var (year, season) = SeasonHelper.GetCurrentSeason();
                var current = TimeMachineYearOptions
                    .Select(yearsAgo => new PastSeasonTarget(year - yearsAgo, season))
                    .ToHashSet();
                var stored = await _tracking.LoadTimeMachineShownAsync();
                var updated = stored
                    .Where(pair => current.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
                var existing = updated.GetValueOrDefault(target, []);
                var ids = replace
                    ? animeIds.Distinct().ToList()
                    : existing.Concat(animeIds).Distinct().ToList();
                // 重新打开页面时会再记一遍同一批，没有新作品、也没有过期的季度要去掉时不写。
                if (!replace && ids.Count == existing.Count && updated.Count == stored.Count)
                    return;

                updated[target] = ids;
                await _tracking.SaveTimeMachineShownAsync(updated);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CurrentSeasonViewModel] RecordTimeMachineShownAsync failed: {ex.Message}");
            }
            finally
            {
                TimeMachineShownGate.Release();
            }
        }

        /// <summary>
        /// 第一次抽一季时的顺序：没显示过的在前、显示过的在后，两组各自打乱。
        /// 全部都显示过时整体重新打乱，并通过 <paramref name="startOver"/> 告知调用方清空记录、从头再来。
        /// </summary>
        internal static IReadOnlyList<TimeMachineEntry> OrderForDisplay(
            IReadOnlyList<TimeMachineEntry> pool,
            IReadOnlySet<int> shown,
            Random random,
            out bool startOver)
        {
            var unseen = pool.Where(entry => !shown.Contains(entry.Anime.ID)).ToArray();
            if (unseen.Length == 0)
            {
                startOver = pool.Count > 0;
                var all = pool.ToArray();
                random.Shuffle(all);
                return all;
            }

            startOver = false;
            var seen = pool.Where(entry => shown.Contains(entry.Anime.ID)).ToArray();
            random.Shuffle(unseen);
            random.Shuffle(seen);
            return [.. unseen, .. seen];
        }

        /// <summary>
        /// 沿用已抽到的顺序；已不在候选里的（例如刚被屏蔽）去掉，
        /// 没抽过的候选打乱后接在后面。一部都没抽过时就是整体随机打乱。
        /// </summary>
        internal static IReadOnlyList<TimeMachineEntry> KeepStoredOrder(
            IReadOnlyList<TimeMachineEntry> pool,
            IReadOnlyList<int> storedIds,
            Random random)
        {
            var byId = pool.ToDictionary(entry => entry.Anime.ID);
            var kept = storedIds
                .Where(byId.ContainsKey)
                .Select(id => byId[id])
                .ToList();
            var keptIds = kept.Select(entry => entry.Anime.ID).ToHashSet();
            var rest = pool.Where(entry => !keptIds.Contains(entry.Anime.ID)).ToArray();
            random.Shuffle(rest);
            return [.. kept, .. rest];
        }

        /// <summary>候选：有评分的作品按评分从高到低；屏蔽的不显示，其余标记保留。</summary>
        internal static IReadOnlyList<TimeMachineEntry> RankTimeMachine(
            IEnumerable<Anime> season,
            IReadOnlyDictionary<int, AnimeTrackingStatus> statuses)
            => season
                .DistinctBy(anime => anime.ID)
                .Where(anime => anime.Score is > 0
                    && statuses.GetValueOrDefault(anime.ID) != AnimeTrackingStatus.Blocked)
                .OrderByDescending(anime => anime.Score)
                .ThenBy(anime => anime.Title, TitleComparer)
                .Select((anime, index) => new TimeMachineEntry(index + 1, anime)
                {
                    Status = statuses.GetValueOrDefault(anime.ID),
                })
                .ToList();
    }
}
