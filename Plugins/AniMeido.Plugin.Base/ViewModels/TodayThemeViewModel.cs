using System.Collections.ObjectModel;
using System.Globalization;
using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AniMeido.Plugin.Base.ViewModels
{
    /// <summary>今日主题里的一行：作品、说明与你的标记。</summary>
    public sealed partial class TodayThemeItem(
        Anime anime,
        string meta,
        string note,
        TodayThemeRowKind rowKind) : ObservableObject
    {
        private static readonly IReadOnlyDictionary<AnimeTrackingStatus, string> StatusLabels =
            TrackingActionDescriptor.CreateDefaults()
                .ToDictionary(action => action.Status, action => action.ActiveLabel);

        public Anime Anime { get; } = anime;

        public string Meta { get; } = meta;

        /// <summary>这部作品为什么出现，例如“停了 41 天”。</summary>
        public string Note { get; } = note;

        public bool HasNote => Note.Length > 0;

        public TodayThemeRowKind RowKind { get; } = rowKind;

        public string ScoreText => Anime.Score is { } score
            ? score.ToString("F1", CultureInfo.InvariantCulture)
            : "";

        [ObservableProperty]
        private AnimeTrackingStatus _status;

        [ObservableProperty]
        private bool _isSavingStatus;

        /// <summary>已标记时显示的状态，例如“补番中”。</summary>
        public string StatusText => StatusLabels.GetValueOrDefault(Status, "");

        public bool HasStatus => StatusText.Length > 0;

        /// <summary>没有标记时行尾显示评分。</summary>
        public bool ShowScore => !HasStatus && ScoreText.Length > 0;

        /// <summary>第一个按钮写入的状态；为 null 时表示打开档案馆评分，或没有按钮。</summary>
        public AnimeTrackingStatus? PrimaryStatus => RowKind switch
        {
            TodayThemeRowKind.Mark => AnimeTrackingStatus.PlanToWatch,
            TodayThemeRowKind.Finish => AnimeTrackingStatus.Completed,
            _ => null,
        };

        public AnimeTrackingStatus? SecondaryStatus => RowKind switch
        {
            TodayThemeRowKind.Mark => AnimeTrackingStatus.Following,
            TodayThemeRowKind.Finish => AnimeTrackingStatus.Dropped,
            _ => null,
        };

        public string PrimaryLabel => RowKind switch
        {
            TodayThemeRowKind.Mark => Status == AnimeTrackingStatus.PlanToWatch ? "补番中 ✓" : "补番",
            TodayThemeRowKind.Rate => "评分",
            TodayThemeRowKind.Finish => "看完了",
            _ => "",
        };

        public string SecondaryLabel => RowKind switch
        {
            TodayThemeRowKind.Mark => Status == AnimeTrackingStatus.Following ? "关注中 ✓" : "关注",
            TodayThemeRowKind.Finish => "弃番",
            _ => "",
        };

        /// <summary>“看完了 / 弃番”只在还在看的时候出现；其余有操作的行总是显示。</summary>
        public bool HasPrimary => RowKind switch
        {
            TodayThemeRowKind.Mark or TodayThemeRowKind.Rate => true,
            TodayThemeRowKind.Finish => Status is AnimeTrackingStatus.Watching or AnimeTrackingStatus.PlanToWatch,
            _ => false,
        };

        public bool HasSecondary => RowKind == TodayThemeRowKind.Mark
            || (RowKind == TodayThemeRowKind.Finish && HasPrimary);

        public bool HasActions => HasPrimary || HasSecondary;

        /// <summary>写入中按钮变淡；按钮仍接住点击，不禁用，避免焦点被挤走。</summary>
        public double ActionOpacity => IsSavingStatus
            ? CurrentSeasonViewModel.SavingActionOpacity
            : 1;

        partial void OnStatusChanged(AnimeTrackingStatus value)
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(HasStatus));
            OnPropertyChanged(nameof(ShowScore));
            OnPropertyChanged(nameof(PrimaryLabel));
            OnPropertyChanged(nameof(SecondaryLabel));
            OnPropertyChanged(nameof(HasPrimary));
            OnPropertyChanged(nameof(HasSecondary));
            OnPropertyChanged(nameof(HasActions));
        }

        partial void OnIsSavingStatusChanged(bool value) => OnPropertyChanged(nameof(ActionOpacity));
    }

    /// <summary>轮换进度的一个小点。</summary>
    public sealed record TodayThemeDot(bool IsCurrent)
    {
        public bool IsOther => !IsCurrent;
    }

    /// <summary>
    /// 今天页的“今日主题”：每天按日期轮到一个主题，列出一批作品；一天内主题不变，只能换一批。
    /// 当天的主题和这一批存进本地，同一天重启后按它恢复。
    /// 这是今天页的补充内容，慢或失败都不影响页面其他部分。
    /// </summary>
    public sealed partial class TodayThemeViewModel : ObservableObject
    {
        internal const int BatchSize = 6;

        // 只用 Bangumi 数据的主题，候选在一次运行中按“日期 + 主题”缓存：切换页面再回来不重新请求。
        // 依赖个人数据的主题不缓存：评了分、改了标记，下次加载就按新的记录计算。
        private static readonly object CacheGate = new();
        private static readonly Dictionary<(DateOnly Date, TodayThemeKind Kind), TodayThemeContent> ContentCache = [];

        private readonly TodayThemeService _service;
        private readonly TrackingService _tracking;
        private CancellationTokenSource? _cts;
        // 本次加载成功得到的全部候选，按轮换顺序排列。
        private IReadOnlyList<TodayThemeItem> _order = [];
        private DateOnly _date;
        private TodayThemeKind _shownKind;
        private bool _loaded;
        private bool _usesPersonalData;

        internal TodayThemeViewModel(
            IAnimeDataSource dataSource,
            TrackingService tracking,
            BrowseHistoryService browseHistory,
            ArchiveService archive,
            ActionCenterService actionCenter)
        {
            _service = new TodayThemeService(dataSource, tracking, browseHistory, archive, actionCenter);
            _tracking = tracking;
        }

        [ObservableProperty]
        private string _title = "";

        [ObservableProperty]
        private string _caption = "";

        [ObservableProperty]
        private string _sourceText = "";

        [ObservableProperty]
        private bool _isFallback;

        [ObservableProperty]
        private ObservableCollection<TodayThemeItem> _items = [];

        [ObservableProperty]
        private IReadOnlyList<TodayThemeDot> _cycleDots = [];

        [ObservableProperty]
        private string _nextThemeText = "";

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private bool _isFailed;

        [ObservableProperty]
        private bool _canShowNextBatch;

        public string StatusText => IsLoading
            ? "正在准备今天的主题…"
            : IsFailed
                ? "没有加载出来，点“重试”再试一次"
                : _loaded && Items.Count == 0 ? "这个主题今天没有可显示的作品" : "";

        public bool HasStatusText => StatusText.Length > 0;

        /// <summary>这一次加载，测试用来等待它完成。</summary>
        internal Task PendingLoad { get; private set; } = Task.CompletedTask;

        partial void OnItemsChanged(ObservableCollection<TodayThemeItem> value) => RaiseStatusText();

        partial void OnIsLoadingChanged(bool value) => RaiseStatusText();

        partial void OnIsFailedChanged(bool value) => RaiseStatusText();

        private void RaiseStatusText()
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(HasStatusText));
        }

        /// <summary>测试用：清空本次运行中缓存的候选与题材搜索结果。</summary>
        internal static void ResetSessionCache()
        {
            lock (CacheGate)
            {
                ContentCache.Clear();
            }

            TodayThemeService.ResetSessionCache();
        }

        /// <summary>加载今天的主题。重复调用会取消上一次。</summary>
        public Task LoadAsync() => PendingLoad = LoadForDateAsync(DateOnly.FromDateTime(DateTime.Today));

        [RelayCommand]
        private Task RetryAsync() => LoadAsync();

        /// <summary>页面离开时调用：取消正在进行的加载，结果不再写回。</summary>
        public void CancelLoad()
        {
            if (_cts is not { } cts)
                return;

            _cts = null;
            cts.Cancel();
            cts.Dispose();
            IsLoading = false;
        }

        /// <summary>
        /// 页面回到前台时调用（例如从详情页或档案馆返回，页面实例被复用）：
        /// 被取消、还没有结果的加载重新开始；正在显示依赖个人数据的主题时重新计算，
        /// 刚在别处评了分、改了标记的作品随之更新。失败的等用户点重试。
        /// </summary>
        public void ResumeIfNeeded()
        {
            if (_cts is not null || IsFailed)
                return;

            if (!_loaded || _usesPersonalData)
                _ = LoadAsync();
        }

        internal async Task LoadForDateAsync(DateOnly date)
        {
            CancelLoad();
            var cts = new CancellationTokenSource();
            _cts = cts;
            // 刷新同一天的主题时，加载期间保留原来的标题。
            var refreshing = _loaded && _date == date;
            // 同一天刷新（例如刚在这里标记完、页面随之刷新）时保留当前这一批，刚标记的不会消失。
            var keep = refreshing
                ? Items.Select(item => item.Anime.ID).ToList()
                : [];
            _loaded = false;
            IsFailed = false;
            IsLoading = true;

            var (kind, position) = TodayThemeSchedule.For(date);
            if (!refreshing)
            {
                Title = TodayThemeCatalog.GetTitle(kind);
                Caption = "";
                SourceText = TodayThemeCatalog.GetSourceText(kind);
                IsFallback = false;
            }

            CycleDots = Enumerable.Range(0, TodayThemeSchedule.Count)
                .Select(index => new TodayThemeDot(index == position))
                .ToList();
            NextThemeText = $"明天：{TodayThemeCatalog.GetTitle(TodayThemeSchedule.For(date.AddDays(1)).Kind)}";
            try
            {
                var rows = await _tracking.GetAllTrackingAsync();
                var statuses = rows.ToDictionary(row => row.AnimeId, row => row.Status);
                var dailyPick = await TryLoadDailyPickAsync();
                var context = new TodayThemeContext(
                    date,
                    statuses,
                    dailyPick is { } pick && pick.Date == date ? pick.AnimeId : null)
                {
                    UpdatedAt = rows
                        .Select(row => (row.AnimeId, At: TodayThemeService.ParseTimestamp(row.UpdatedAt)))
                        .Where(row => row.At is not null)
                        .ToDictionary(row => row.AnimeId, row => row.At!.Value),
                };

                var shownKind = kind;
                var isFallback = false;
                var content = await GetContentAsync(kind, context, cts.Token);
                // 轮到的就是替补主题时不再重复查询，直接显示它的空状态。
                if (content is null && kind != TodayThemeCatalog.Fallback)
                {
                    shownKind = TodayThemeCatalog.Fallback;
                    isFallback = true;
                    content = await GetContentAsync(shownKind, context, cts.Token);
                }

                if (cts.IsCancellationRequested)
                    return;

                Title = TodayThemeCatalog.GetTitle(shownKind);
                Caption = isFallback
                    ? $"今天轮到“{TodayThemeCatalog.GetTitle(kind)}”，但暂时没有符合条件的作品"
                    : content?.Caption ?? "";
                SourceText = TodayThemeCatalog.GetSourceText(shownKind);
                _usesPersonalData = TodayThemeCatalog.UsesPersonalData(shownKind);
                IsFallback = isFallback;

                _order = BuildOrder(content, context, keep);
                _date = date;
                _shownKind = shownKind;
                CanShowNextBatch = CountSelectable() > BatchSize;

                var stored = await TryLoadStateAsync();
                if (cts.IsCancellationRequested)
                    return;

                var ids = _order.Select(item => item.Anime.ID).ToList();
                var batch = keep.Count > 0
                    ? RestoreBatch(ids, keep, BatchSize, id => keep.Contains(id) || IsSelectable(id))
                    : stored is { } state
                        && state.Date == date
                        && state.Theme == shownKind
                        && state.IsFallback == isFallback
                            ? RestoreBatch(ids, state.AnimeIds, BatchSize, IsSelectable)
                            : NextBatch(ids, [], BatchSize, IsSelectable);
                ShowBatch(batch);
                _loaded = true;
                await TrySaveStateAsync();
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
#pragma warning disable CA1031 // 今日主题是补充内容，失败只显示重试，不打断今天页
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[TodayThemeViewModel] LoadForDateAsync failed: {ex.Message}");
                if (ReferenceEquals(_cts, cts))
                {
                    Items = [];
                    IsFailed = true;
                }
            }
#pragma warning restore CA1031
            finally
            {
                if (ReferenceEquals(_cts, cts))
                {
                    _cts = null;
                    cts.Dispose();
                    IsLoading = false;
                }
            }
        }

        /// <summary>换一批：在同一主题里往后取，取完回到开头。</summary>
        [RelayCommand]
        private async Task NextBatchAsync()
        {
            if (!_loaded)
                return;

            ShowBatch(NextBatch(
                _order.Select(item => item.Anime.ID).ToList(),
                Items.Select(item => item.Anime.ID).ToList(),
                BatchSize,
                IsSelectable));
            await TrySaveStateAsync();
        }

        /// <summary>
        /// 执行行上的标记操作。“补番 / 关注”再点一次取消；“看完了 / 弃番”直接写入。
        /// 上一次还没写完时不执行，返回 false；写入失败时抛出，由页面提示。
        /// </summary>
        public async Task<bool> ApplyStatusAsync(TodayThemeItem item, AnimeTrackingStatus status)
        {
            if (item.IsSavingStatus)
                return false;

            item.IsSavingStatus = true;
            try
            {
                if (item.RowKind == TodayThemeRowKind.Mark && item.Status == status)
                {
                    await _tracking.RemoveStatusAsync(item.Anime.ID);
                    item.Status = AnimeTrackingStatus.None;
                }
                else
                {
                    await _tracking.SetStatusAsync(item.Anime.ID, status);
                    item.Status = status;
                }

                // 标记过的作品不再换进来，“换一批”是否可用随之变化。
                CanShowNextBatch = CountSelectable() > BatchSize;
                return true;
            }
            finally
            {
                item.IsSavingStatus = false;
            }
        }

        private async Task<TodayThemeContent?> GetContentAsync(
            TodayThemeKind kind,
            TodayThemeContext context,
            CancellationToken ct)
        {
            var cacheable = !TodayThemeCatalog.UsesPersonalData(kind);
            lock (CacheGate)
            {
                if (cacheable && ContentCache.TryGetValue((context.Date, kind), out var cached))
                    return cached;
            }

            var content = await _service.BuildAsync(kind, context, ct);
            if (cacheable && content is not null)
            {
                lock (CacheGate)
                {
                    ContentCache[(context.Date, kind)] = content;
                }
            }

            return content;
        }

        /// <summary>
        /// 候选按日期打乱（或保持给出的顺序），并带上最新的标记：
        /// 缓存里的候选可能是标记之前取的，可标记的主题里已标记的去掉（<paramref name="keep"/> 里的除外），
        /// 屏蔽的一律去掉。
        /// </summary>
        internal static IReadOnlyList<TodayThemeItem> BuildOrder(
            TodayThemeContent? content,
            TodayThemeContext context,
            IReadOnlyCollection<int> keep)
        {
            if (content is null)
                return [];

            var candidates = content.Candidates
                .DistinctBy(candidate => candidate.Anime.ID)
                .Where(candidate => content.RowKind != TodayThemeRowKind.Mark
                    || keep.Contains(candidate.Anime.ID)
                    || (context.Statuses.GetValueOrDefault(candidate.Anime.ID) == AnimeTrackingStatus.None
                        && candidate.Anime.ID != context.ExcludedAnimeId))
                .Where(candidate => context.Statuses.GetValueOrDefault(candidate.Anime.ID)
                    != AnimeTrackingStatus.Blocked)
                .ToDictionary(candidate => candidate.Anime.ID);
            var ids = content.ShuffleByDate
                ? StableShuffle.ByDate(candidates.Keys, context.Date)
                : content.Candidates.Select(candidate => candidate.Anime.ID)
                    .Where(candidates.ContainsKey)
                    .Distinct()
                    .ToList();
            return ids
                .Select(id => candidates[id])
                .Select(candidate => new TodayThemeItem(
                    candidate.Anime,
                    candidate.Meta,
                    candidate.Note,
                    content.RowKind)
                {
                    Status = context.Statuses.GetValueOrDefault(candidate.Anime.ID),
                })
                .ToList();
        }

        /// <summary>可标记的主题里，已经标记过的作品不再换进来；其余主题的作品都可以。</summary>
        private bool IsSelectable(int animeId)
            => _order.FirstOrDefault(item => item.Anime.ID == animeId) is { } item
                && (item.RowKind != TodayThemeRowKind.Mark || item.Status == AnimeTrackingStatus.None);

        private int CountSelectable() => _order.Count(item => IsSelectable(item.Anime.ID));

        /// <summary>显示一批；高分类主题一批内按评分从高到低排列。</summary>
        private void ShowBatch(IReadOnlyList<int> ids)
        {
            var byId = _order.ToDictionary(item => item.Anime.ID);
            var batch = ids.Where(byId.ContainsKey).Select(id => byId[id]);
            Items = new ObservableCollection<TodayThemeItem>(
                _order.FirstOrDefault()?.RowKind == TodayThemeRowKind.Mark
                    ? batch.OrderByDescending(item => item.Anime.Score ?? 0)
                    : batch);
            CanShowNextBatch = CountSelectable() > BatchSize;
        }

        /// <summary>
        /// 按记下的作品恢复这一批：还在候选里、仍可显示的保留；
        /// 不足的从第一部保留作品的位置往后补，找不到位置时从头补。
        /// </summary>
        internal static IReadOnlyList<int> RestoreBatch(
            IReadOnlyList<int> order,
            IReadOnlyList<int> stored,
            int size,
            Func<int, bool> isSelectable)
        {
            var available = order.ToHashSet();
            var kept = stored
                .Where(id => available.Contains(id) && isSelectable(id))
                .Distinct()
                .Take(size)
                .ToList();
            if (kept.Count == size || order.Count == 0)
                return kept;

            var start = kept.Count > 0 ? IndexOf(order, kept[0]) : 0;
            var result = new List<int>(kept);
            for (var step = 0; step < order.Count && result.Count < size; step++)
            {
                var id = order[(start + step) % order.Count];
                if (!result.Contains(id) && isSelectable(id))
                    result.Add(id);
            }

            return result;
        }

        /// <summary>
        /// 下一批：从当前这一批最后一部之后往后取（到末尾回到开头），跳过不能显示的；
        /// 当前为空时从头取。候选不超过一批时就是全部。
        /// </summary>
        internal static IReadOnlyList<int> NextBatch(
            IReadOnlyList<int> order,
            IReadOnlyList<int> current,
            int size,
            Func<int, bool> isSelectable)
        {
            if (order.Count == 0)
                return [];

            var start = current.Count > 0 && IndexOf(order, current[^1]) is var last and >= 0
                ? last + 1
                : 0;
            var result = new List<int>(size);
            for (var step = 0; step < order.Count && result.Count < size; step++)
            {
                var id = order[(start + step) % order.Count];
                if (isSelectable(id) && !result.Contains(id))
                    result.Add(id);
            }

            return result;
        }

        private static int IndexOf(IReadOnlyList<int> order, int id)
        {
            for (var index = 0; index < order.Count; index++)
            {
                if (order[index] == id)
                    return index;
            }

            return -1;
        }

        private async Task<(DateOnly Date, int AnimeId)?> TryLoadDailyPickAsync()
        {
            try
            {
                return await _tracking.LoadCalendarDailyPickAsync();
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[TodayThemeViewModel] LoadCalendarDailyPickAsync failed: {ex.Message}");
                return null;
            }
        }

        private async Task<TodayThemeState?> TryLoadStateAsync()
        {
            try
            {
                return await _tracking.LoadTodayThemeAsync();
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[TodayThemeViewModel] LoadTodayThemeAsync failed: {ex.Message}");
                return null;
            }
        }

        private async Task TrySaveStateAsync()
        {
            try
            {
                await _tracking.SaveTodayThemeAsync(new TodayThemeState(
                    _date,
                    _shownKind,
                    IsFallback,
                    Items.Select(item => item.Anime.ID).ToList()));
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                // 记不下来只影响重启后能否回到这一批，不打断页面。
                System.Diagnostics.Debug.WriteLine(
                    $"[TodayThemeViewModel] SaveTodayThemeAsync failed: {ex.Message}");
            }
        }
    }
}
