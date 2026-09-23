using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.Json;
using System.Collections.ObjectModel;

namespace AniMeido.Plugin.Base.ViewModels
{
    public partial class PastSeasonViewModel : ObservableObject
    {
        /// <summary>网格当前显示的作品：已筛选、已排序。</summary>
        [ObservableProperty]
        private ObservableCollection<PastSeasonEntry> _entries = [];
        [ObservableProperty]
        private IReadOnlyList<PastSeasonFormatChip> _formatChips = [];
        [ObservableProperty]
        private bool _isLoading = false;
        [ObservableProperty]
        string? _errorMessage = null;
        [ObservableProperty]
        private bool _hasData = false;
        [ObservableProperty]
        private bool _isError = false;
        /// <summary>标题行的季度与计数，如“2026 年春季 · 247 部”。</summary>
        [ObservableProperty]
        private string _summaryText = "";
        /// <summary>加载完成、没有出错，但网格为空。</summary>
        [ObservableProperty]
        private bool _hasNoResults = false;
        /// <summary>网格为空是筛选造成的（季度本身有作品），可以清除筛选。</summary>
        [ObservableProperty]
        private bool _canClearFilters = false;
        [ObservableProperty]
        private string _emptyText = "";

        /// <summary>当前季度的全部作品（未筛选）。</summary>
        public IReadOnlyList<Anime> LoadedAnime { get; private set; } = [];

        /// <summary>正在显示或正在加载的季度。</summary>
        public PastSeasonTarget? Season { get; private set; }

        public string Query { get; private set; } = "";
        public AnimeMediaFormat? SelectedFormat { get; private set; }
        public PastSeasonSortKey SortKey { get; private set; } = PastSeasonSortKey.Score;
        public bool SortAscending { get; private set; } = PastSeasonBrowse.DefaultAscending(PastSeasonSortKey.Score);
        public bool HideCompleted { get; private set; }

        private readonly IAnimeDataSource _animeDataSource;
        private readonly TrackingService _tracking;
        private IReadOnlyDictionary<int, AnimeTrackingStatus> _statuses =
            new Dictionary<int, AnimeTrackingStatus>();
        private int _loadVersion;



        public PastSeasonViewModel(IAnimeDataSource dataSource, TrackingService tracking)
        {
            _animeDataSource = dataSource;
            _tracking = tracking;
        }



        /// <summary>
        /// 加载往季番剧页面数据。换季度时保留形态、排序与“隐藏看过”，清空搜索词。
        /// </summary>
        /// <param name="year">要加载的年份</param>
        /// <param name="season">要加载的季度</param>
        public async Task LoadPastSeasonAnimeAsync(int year, Season season, CancellationToken ct = default)
        {
            var version = Interlocked.Increment(ref _loadVersion);
            var target = new PastSeasonTarget(year, season);
            if (Season != target)
                Query = "";
            Season = target;
            IsLoading = true;
            IsError = false;
            ErrorMessage = null;
            LoadedAnime = [];
            HasData = false;
            Refresh();

            try
            {
                var list = await _animeDataSource.GetAnimeBySeasonAsync(
                    year,
                    season,
                    ct);
                if (!IsCurrentLoad(version, ct))
                    return;

                LoadedAnime = list.ToArray();
            }
            catch (HttpRequestException ex)
            {
                if (!IsCurrentLoad(version, ct))
                    return;

                ErrorMessage = $"网络请求失败：{ex.Message}";
                IsError = true;
            }
            catch (BangumiApiException ex)
            {
                if (!IsCurrentLoad(version, ct))
                    return;

                ErrorMessage = $"数据源请求失败：{ex.Message}";
                IsError = true;
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                // 用户切换年份/季度引起的取消，静默忽略
                return;
            }
            catch (TaskCanceledException)
            {
                if (!IsCurrentLoad(version, ct))
                    return;

                // HTTP 超时或其他网络层取消，作为错误处理
                ErrorMessage = "网络请求超时，请检查网络后重试";
                IsError = true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException)
            {
                if (!IsCurrentLoad(version, ct))
                    return;

                ErrorMessage = $"数据解析失败：{ex.Message}";
                IsError = true;
            }
            finally
            {
                // 取消的请求不修改 IsLoading，新请求已设置自己的状态
                if (version == Volatile.Read(ref _loadVersion))
                {
                    IsLoading = false;
                    Refresh();
                }
            }
        }

        private bool IsCurrentLoad(int version, CancellationToken ct) =>
            version == Volatile.Read(ref _loadVersion) && !ct.IsCancellationRequested;

        /// <summary>
        /// 读取追番状态：卡片标签、屏蔽与“隐藏看过”都依赖它。
        /// 读取失败时保留已有状态，返回 false。
        /// </summary>
        public async Task<bool> ReloadStatusesAsync()
        {
            try
            {
                _statuses = (await _tracking.GetAllTrackingAsync())
                    .ToDictionary(row => row.AnimeId, row => row.Status);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[PastSeasonViewModel] ReloadStatusesAsync failed: {ex.Message}");
                return false;
            }

            Refresh();
            return true;
        }

        public void SetQuery(string query)
        {
            if (Query == query)
                return;

            Query = query;
            Refresh();
        }

        public void SelectFormat(AnimeMediaFormat? format)
        {
            if (SelectedFormat == format)
                return;

            SelectedFormat = format;
            Refresh();
        }

        /// <summary>换排序方式时方向回到该方式的默认值。</summary>
        public void SetSortKey(PastSeasonSortKey key)
        {
            if (SortKey == key)
                return;

            SortKey = key;
            SortAscending = PastSeasonBrowse.DefaultAscending(key);
            OnPropertyChanged(nameof(SortAscending));
            Refresh();
        }

        public void ToggleSortDirection()
        {
            SortAscending = !SortAscending;
            OnPropertyChanged(nameof(SortAscending));
            Refresh();
        }

        public void SetHideCompleted(bool hide)
        {
            if (HideCompleted == hide)
                return;

            HideCompleted = hide;
            Refresh();
        }

        /// <summary>清除搜索、形态与“隐藏看过”；排序不算筛选，保留。</summary>
        public void ClearFilters()
        {
            Query = "";
            SelectedFormat = null;
            HideCompleted = false;
            OnPropertyChanged(nameof(Query));
            OnPropertyChanged(nameof(HideCompleted));
            Refresh();
        }

        /// <summary>按当前季度、状态与筛选条件重算网格、形态标签与计数。</summary>
        private void Refresh()
        {
            var visible = PastSeasonBrowse.Visible(LoadedAnime, _statuses, Query, HideCompleted);
            FormatChips = PastSeasonBrowse.FormatChips(visible, SelectedFormat);
            Entries = new ObservableCollection<PastSeasonEntry>(PastSeasonBrowse.Sort(
                PastSeasonBrowse.ByFormat(visible, SelectedFormat),
                SortKey,
                SortAscending));

            var total = LoadedAnime.Count(anime =>
                _statuses.GetValueOrDefault(anime.ID) != AnimeTrackingStatus.Blocked);
            HasData = total > 0;
            SummaryText = Season is not { } season
                ? ""
                : IsLoading
                    ? $"{season.Year} 年{PastSeasonBrowse.SeasonName(season.Season)}季 · 加载中"
                    : IsError
                        ? $"{season.Year} 年{PastSeasonBrowse.SeasonName(season.Season)}季"
                        : PastSeasonBrowse.Summary(season, Entries.Count, total);

            HasNoResults = !IsLoading && !IsError && Season is not null && Entries.Count == 0;
            CanClearFilters = HasNoResults && HasData;
            EmptyText = HasData ? "没有符合条件的作品" : "该季度暂无番剧数据";
        }
    }
}
