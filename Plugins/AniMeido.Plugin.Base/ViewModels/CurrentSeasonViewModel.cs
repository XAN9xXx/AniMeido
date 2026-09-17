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
    public sealed partial class CalendarEntry(Anime anime) : ObservableObject
    {
        public Anime Anime { get; } = anime;

        public string WeekdayText { get; } =
            AnimeListPresentation.GetWeekdayName(anime.Weekday);

        [ObservableProperty]
        private AnimeTrackingStatus _status;

        public bool IsMine => Status is AnimeTrackingStatus.Watching
            or AnimeTrackingStatus.Following;
    }

    /// <summary>星期格上的一个小点：追番中或关注中。</summary>
    public sealed record CalendarDot(bool IsFollowing)
    {
        public bool IsWatching => !IsFollowing;
    }

    /// <summary>顶部七个星期格之一。</summary>
    public sealed partial class CalendarDay(
        int weekday,
        string label,
        bool isToday) : ObservableObject
    {
        public int Weekday { get; } = weekday;

        public string Label { get; } = label;

        public bool IsToday { get; } = isToday;

        [ObservableProperty]
        private string _countText = "";

        [ObservableProperty]
        private IReadOnlyList<CalendarDot> _dots = [];

        [ObservableProperty]
        private bool _isSelected;
    }

    /// <summary>“本季发现”中的一行。</summary>
    public sealed record CalendarPick(int Rank, CalendarEntry Entry)
    {
        public string RankText => Rank.ToString(CultureInfo.InvariantCulture);

        public string ScoreText => Entry.Anime.Score is { } score
            ? score.ToString("F1", CultureInfo.InvariantCulture)
            : "";

        public string Meta => Entry.Anime.MediaFormat == AnimeMediaFormat.Unknown
            ? Entry.WeekdayText
            : $"{Entry.WeekdayText} · {AnimeReleaseClassifier.GetMediaFormatText(Entry.Anime.MediaFormat)}";
    }

    public partial class CurrentSeasonViewModel : ObservableObject
    {
        private static readonly StringComparer TitleComparer =
            StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), ignoreCase: true);

        private readonly IAnimeDataSource _animeDataSource;
        private readonly TrackingService _tracking;
        private IReadOnlyList<CalendarEntry> _entries = [];
        private bool _suppressRefresh;
        private int _discoverCapacity;

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
        private ObservableCollection<CalendarPick> _discoverPicks = [];

        [ObservableProperty]
        private string _seasonSummary = "按日历排期，不代表已上线";

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
                Enumerable.Range(1, 7).Select(weekday => new CalendarDay(
                    weekday,
                    AnimeListPresentation.GetWeekdayName(weekday),
                    weekday == today)));
        }

        public ObservableCollection<CalendarDay> Days { get; }

        /// <summary>搜索有内容或只看“我的”时，结果跨越多天，改用网格显示。</summary>
        public bool IsFiltering =>
            ShowMineOnly || !string.IsNullOrWhiteSpace(SearchText);

        public bool HasNoVisibleEntries => VisibleEntries.Count == 0;

        public bool HasDiscoverPicks => DiscoverPicks.Count > 0;

        partial void OnSearchTextChanged(string value)
        {
            OnPropertyChanged(nameof(IsFiltering));
            if (!_suppressRefresh)
                Refresh(rebuildList: true);
        }

        partial void OnShowMineOnlyChanged(bool value)
        {
            OnPropertyChanged(nameof(IsFiltering));
            if (!_suppressRefresh)
                Refresh(rebuildList: true);
        }

        partial void OnSortChanged(CalendarSort value) => Refresh(rebuildList: true);

        partial void OnVisibleEntriesChanged(ObservableCollection<CalendarEntry> value)
            => OnPropertyChanged(nameof(HasNoVisibleEntries));

        partial void OnDiscoverPicksChanged(ObservableCollection<CalendarPick> value)
            => OnPropertyChanged(nameof(HasDiscoverPicks));

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
        }

        /// <summary>按剩余高度能完整放下的行数更新“本季发现”的条数。</summary>
        public void SetDiscoverCapacity(int capacity)
        {
            capacity = Math.Max(0, capacity);
            if (capacity == _discoverCapacity)
            {
                return;
            }

            _discoverCapacity = capacity;
            RefreshDiscover();
        }

        /// <summary>与详情页一致：已是该状态则取消，否则设为该状态。</summary>
        public async Task ToggleStatusAsync(int animeId, AnimeTrackingStatus status)
        {
            var entry = _entries.FirstOrDefault(item => item.Anime.ID == animeId);
            if (entry is null)
            {
                return;
            }

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

            // 只看“我的”时成员会变化，需要重建；否则保留列表顺序与滚动位置。
            Refresh(rebuildList: ShowMineOnly);
        }

        /// <summary>
        /// 重新读取本地标记（例如拖放标记或从详情页返回后）。新屏蔽的作品会被移除。
        /// </summary>
        public async Task ReloadStatusesAsync()
        {
            if (_entries.Count == 0)
            {
                return;
            }

            var statuses = await ReadStatusesAsync();
            var blockedRemoved = _entries.Any(entry =>
                statuses.GetValueOrDefault(entry.Anime.ID) == AnimeTrackingStatus.Blocked);
            if (blockedRemoved)
            {
                _entries = BuildEntries(_entries.Select(entry => entry.Anime), statuses);
            }
            else
            {
                foreach (var entry in _entries)
                {
                    entry.Status = statuses.GetValueOrDefault(entry.Anime.ID);
                }
            }

            Refresh(rebuildList: blockedRemoved || ShowMineOnly);
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
            IsLoading = true;
            IsError = false;
            ErrorMessage = null;
            HasData = false;
            try
            {
                var scheduleTask = _animeDataSource
                    .GetCurrentBroadcastScheduleAsync(ct);
                var statuses = await ReadStatusesAsync();
                var schedule = await scheduleTask;
                ct.ThrowIfCancellationRequested();

                _entries = BuildEntries(schedule, statuses);
                HasData = _entries.Count > 0;
                Refresh(rebuildList: true);
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
                if (!ct.IsCancellationRequested)
                    IsLoading = false;
            }
        }

        private async Task<IReadOnlyDictionary<int, AnimeTrackingStatus>> ReadStatusesAsync()
        {
            try
            {
                return (await _tracking.GetAllTrackingAsync())
                    .ToDictionary(row => row.AnimeId, row => row.Status);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                // 本地标记读取失败时仍显示放送日程，只是不带状态。
                System.Diagnostics.Debug.WriteLine(
                    $"[CurrentSeasonViewModel] ReadStatusesAsync failed: {ex.Message}");
                return new Dictionary<int, AnimeTrackingStatus>();
            }
        }

        private void Refresh(bool rebuildList)
        {
            var mineCount = _entries.Count(entry => entry.IsMine);
            TotalCountText = _entries.Count.ToString(CultureInfo.InvariantCulture);
            MineCountText = mineCount.ToString(CultureInfo.InvariantCulture);
            var (year, season) = SeasonHelper.GetCurrentSeason();
            SeasonSummary =
                $"{year} 年{GetSeasonName(season)} · 共 {_entries.Count} 部 · 按日历排期，不代表已上线";

            var filtering = IsFiltering;

            var query = SearchText.Trim();
            var matches = _entries
                .Where(entry => !ShowMineOnly || entry.IsMine)
                .Where(entry => query.Length == 0
                    || entry.Anime.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var day in Days)
            {
                var total = _entries.Count(entry => entry.Anime.Weekday == day.Weekday);
                var matched = matches.Count(entry => entry.Anime.Weekday == day.Weekday);
                day.CountText = BuildDayCountText(matched, total, filtering);
                day.Dots = filtering
                    ? []
                    : _entries
                        .Where(entry => entry.Anime.Weekday == day.Weekday && entry.IsMine)
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
            else
            {
                list = _entries
                    .Where(entry => entry.Anime.Weekday == SelectedWeekday)
                    .ToList();
                var dayMine = list.Count(entry => entry.IsMine);
                ListTitle =
                    $"{AnimeListPresentation.GetWeekdayName(SelectedWeekday)} · {list.Count} 部";
                ListCaption = dayMine > 0 ? $"你标记的 {dayMine} 部排在前面" : "";
                EmptyText = "这一天没有排期的番剧";
            }

            if (rebuildList)
            {
                VisibleEntries = new ObservableCollection<CalendarEntry>(
                    Order(list, Sort));
            }

            RefreshDiscover();
        }

        private void RefreshDiscover()
        {
            DiscoverPicks = new ObservableCollection<CalendarPick>(
                RankDiscover(_entries)
                    .Take(_discoverCapacity)
                    .Select((entry, index) => new CalendarPick(index + 1, entry)));
        }

        internal static IReadOnlyList<CalendarEntry> BuildEntries(
            IEnumerable<Anime> schedule,
            IReadOnlyDictionary<int, AnimeTrackingStatus> statuses)
            => schedule
                .DistinctBy(anime => anime.ID)
                .Where(anime => statuses.GetValueOrDefault(anime.ID)
                    != AnimeTrackingStatus.Blocked)
                .Select(anime => new CalendarEntry(anime)
                {
                    Status = statuses.GetValueOrDefault(anime.ID),
                })
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
                    .ThenBy(entry => entry.Anime.Title, TitleComparer),
                _ => mineFirst
                    .ThenByDescending(entry => entry.Anime.Score ?? double.MinValue),
            }).ToList();
        }

        /// <summary>本季发现：还没有任何标记、且有评分的作品，按评分从高到低。</summary>
        internal static IReadOnlyList<CalendarEntry> RankDiscover(
            IEnumerable<CalendarEntry> entries)
            => entries
                .Where(entry => entry.Status == AnimeTrackingStatus.None
                    && entry.Anime.Score is > 0)
                .OrderByDescending(entry => entry.Anime.Score)
                .ThenBy(entry => entry.Anime.Title, TitleComparer)
                .ToList();

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
