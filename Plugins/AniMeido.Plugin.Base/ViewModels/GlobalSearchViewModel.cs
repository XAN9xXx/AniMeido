using System.Collections.ObjectModel;
using System.Text.Json;
using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Data.Sqlite;

namespace AniMeido.Plugin.Base.ViewModels
{
    /// <summary>
    /// 搜索页：先列出“我的番剧”里匹配的作品，再列 Bangumi 结果（加载更多、形态筛选、排序）；
    /// 未搜索时显示最近搜索与收藏的标签。
    /// </summary>
    public partial class GlobalSearchViewModel : ObservableObject
    {
        private readonly IAnimeDataSource _dataSource;
        private readonly TrackingService _tracking;
        private readonly LocalSearchService _localSearch;
        private readonly SavedTagService _savedTags;
        private IReadOnlyList<Anime> _loaded = [];
        private IReadOnlyDictionary<int, AnimeTrackingStatus> _statuses = new Dictionary<int, AnimeTrackingStatus>();
        private int _total;
        private int _nextOffset;
        private CancellationTokenSource? _searchCancellation;
        private int _searchVersion;

        /// <summary>Bangumi 结果：已去掉屏蔽，已按形态筛选和排序。</summary>
        public ObservableCollection<PastSeasonEntry> Results { get; } = [];

        /// <summary>“我的番剧”里匹配的作品。</summary>
        public ObservableCollection<PastSeasonEntry> LocalMatches { get; } = [];

        public ObservableCollection<string> RecentSearches { get; } = [];

        public ObservableCollection<string> SavedTags { get; } = [];

        [ObservableProperty]
        private IReadOnlyList<PastSeasonFormatChip> _formatChips = [];

        /// <summary>最近一次提交的搜索词。</summary>
        [ObservableProperty]
        private string _query = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsLanding))]
        private bool _hasSearched;

        [ObservableProperty]
        private bool _isSearching;

        [ObservableProperty]
        private bool _isLoadingMore;

        [ObservableProperty]
        private bool _canLoadMore;

        [ObservableProperty]
        private bool _isError;

        [ObservableProperty]
        private string? _errorMessage;

        [ObservableProperty]
        private string _summaryText = "";

        [ObservableProperty]
        private bool _hasNoResults;

        [ObservableProperty]
        private string _emptyText = "";

        [ObservableProperty]
        private string _localSummary = "";

        [ObservableProperty]
        private bool _hasLocalMatches;

        public AnimeMediaFormat? SelectedFormat { get; private set; }

        public SearchSortKey SortKey { get; private set; } = SearchSortKey.Match;

        public bool IsLanding => !HasSearched;

        public bool HasRecentSearches => RecentSearches.Count > 0;

        public bool HasSavedTags => SavedTags.Count > 0;

        public bool HasLandingContent => HasRecentSearches || HasSavedTags;

        public GlobalSearchViewModel(
            IAnimeDataSource dataSource,
            TrackingService tracking,
            LocalSearchService localSearch,
            SavedTagService savedTags)
        {
            _dataSource = dataSource;
            _tracking = tracking;
            _localSearch = localSearch;
            _savedTags = savedTags;
            RecentSearches.CollectionChanged += (_, _) => OnLandingChanged();
            SavedTags.CollectionChanged += (_, _) => OnLandingChanged();
        }

        /// <summary>读取最近搜索与收藏的标签（空白首页用）。</summary>
        public async Task LoadLandingAsync()
        {
            Replace(RecentSearches, await _tracking.LoadRecentSearchesAsync());
            Replace(SavedTags, await _savedTags.GetAllSavedTagsAsync());
        }

        /// <summary>返回页面时重读追番状态，卡片标签与屏蔽随之更新。</summary>
        public async Task RefreshStatusesAsync()
        {
            if (!HasSearched || await TryReadStatusesAsync() is not { } statuses)
                return;

            _statuses = statuses;
            for (var index = 0; index < LocalMatches.Count; index++)
            {
                var entry = LocalMatches[index];
                var status = _statuses.GetValueOrDefault(entry.Anime.ID);
                if (status != entry.Status)
                    LocalMatches[index] = entry with { Status = status };
            }

            ApplyResults();
        }

        public async Task SearchAsync(string query)
        {
            var trimmed = query.Trim();
            if (trimmed.Length == 0)
                return;

            var version = BeginSearch(out var token);
            Query = trimmed;
            HasSearched = true;
            IsSearching = true;
            ClearError();
            _loaded = [];
            _total = 0;
            _nextOffset = 0;
            SelectedFormat = null;
            LocalMatches.Clear();
            HasLocalMatches = false;
            ApplyResults();
            await RememberAsync(trimmed);

            try
            {
                if (await TryReadStatusesAsync() is { } statuses)
                    _statuses = statuses;

                var localTask = SearchLocalAsync(trimmed, token);
                var (results, total) = await _dataSource.SearchByKeywordAsync(trimmed, 0, token);
                var local = await localTask;
                if (!IsCurrent(version, token))
                    return;

                Replace(LocalMatches, local);
                HasLocalMatches = LocalMatches.Count > 0;
                LocalSummary = $"{LocalMatches.Count} 部";
                _loaded = SearchBrowse.AppendPage([], results);
                _total = total;
                _nextOffset = results.Count;
            }
            catch (Exception ex) when (HandleException(ex, token))
            {
            }
            finally
            {
                if (version == _searchVersion)
                {
                    IsSearching = false;
                    ApplyResults();
                }
            }
        }

        /// <summary>接着加载下一页；上一页还没回来时不重复请求。</summary>
        public async Task LoadMoreAsync()
        {
            if (!CanLoadMore || IsLoadingMore || IsSearching)
                return;

            var version = _searchVersion;
            var token = _searchCancellation?.Token ?? default;
            IsLoadingMore = true;
            ClearError();
            try
            {
                var (results, total) = await _dataSource.SearchByKeywordAsync(Query, _nextOffset, token);
                if (!IsCurrent(version, token))
                    return;

                _nextOffset += results.Count;
                _loaded = SearchBrowse.AppendPage(_loaded, results);
                // 空页说明已经到底，按实际加载数收尾，避免一直显示“加载更多”。
                _total = results.Count == 0 ? _loaded.Count : total;
            }
            catch (Exception ex) when (HandleException(ex, token))
            {
            }
            finally
            {
                if (version == _searchVersion)
                {
                    IsLoadingMore = false;
                    ApplyResults();
                }
            }
        }

        /// <summary>清空搜索框：回到空白首页。</summary>
        public void ShowLanding()
        {
            BeginSearch(out _);
            HasSearched = false;
            IsSearching = false;
            IsLoadingMore = false;
            ClearError();
        }

        public void SelectFormat(AnimeMediaFormat? format)
        {
            if (SelectedFormat == format)
                return;

            SelectedFormat = format;
            ApplyResults();
        }

        public void SetSortKey(SearchSortKey key)
        {
            if (SortKey == key)
                return;

            SortKey = key;
            ApplyResults();
        }

        public async Task ClearRecentSearchesAsync()
        {
            await _tracking.SaveRecentSearchesAsync([]);
            RecentSearches.Clear();
        }

        public void CancelPendingSearch()
        {
            Interlocked.Increment(ref _searchVersion);
            _searchCancellation?.Cancel();
            _searchCancellation?.Dispose();
            _searchCancellation = null;
            IsSearching = false;
            IsLoadingMore = false;
        }

        private void ApplyResults()
        {
            var visible = PastSeasonBrowse.Visible(_loaded, _statuses, null, hideCompleted: false);
            FormatChips = PastSeasonBrowse.FormatChips(visible, SelectedFormat);
            var shown = SearchBrowse.Sort(PastSeasonBrowse.ByFormat(visible, SelectedFormat), SortKey);
            if (!CollectionSync.SyncInPlace(Results, shown, entry => entry.Anime.ID))
                Replace(Results, shown);

            SummaryText = HasSearched && !IsSearching ? SearchBrowse.Summary(_total, _loaded.Count) : "";
            CanLoadMore = HasSearched && !IsSearching && _nextOffset < _total;
            HasNoResults = HasSearched && !IsSearching && !IsError && Results.Count == 0;
            EmptyText = _loaded.Count == 0 ? $"没有找到与“{Query}”相关的番剧" : "当前筛选下没有作品";
        }

        private async Task<IReadOnlyList<PastSeasonEntry>> SearchLocalAsync(string query, CancellationToken token)
        {
            try
            {
                var results = await _localSearch.SearchTrackedAsync(query, token);
                return results.Select(result => new PastSeasonEntry(result.Anime, result.TrackingStatus)).ToList();
            }
            catch (Exception ex) when (ex is OperationCanceledException
                or HttpRequestException
                or BangumiApiException
                or SqliteException
                or InvalidOperationException
                or JsonException)
            {
                // 本地匹配只是附加信息，失败或被新搜索取消时返回空，不留下未观察的异常。
                return [];
            }
        }

        private async Task RememberAsync(string query)
        {
            var recent = SearchBrowse.PushRecent(RecentSearches.ToList(), query);
            Replace(RecentSearches, recent);
            try
            {
                await _tracking.SaveRecentSearchesAsync(recent);
            }
            catch (SqliteException)
            {
                // 最近搜索只是便利功能，写入失败不影响搜索。
            }
        }

        private async Task<IReadOnlyDictionary<int, AnimeTrackingStatus>?> TryReadStatusesAsync()
        {
            try
            {
                return (await _tracking.GetAllTrackingAsync()).ToDictionary(row => row.AnimeId, row => row.Status);
            }
            catch (SqliteException)
            {
                return null;
            }
        }

        private void OnLandingChanged()
        {
            OnPropertyChanged(nameof(HasRecentSearches));
            OnPropertyChanged(nameof(HasSavedTags));
            OnPropertyChanged(nameof(HasLandingContent));
        }

        private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
        {
            target.Clear();
            foreach (var item in items)
                target.Add(item);
        }

        private int BeginSearch(out CancellationToken token)
        {
            _searchCancellation?.Cancel();
            _searchCancellation?.Dispose();
            _searchCancellation = new CancellationTokenSource();
            token = _searchCancellation.Token;
            return Interlocked.Increment(ref _searchVersion);
        }

        private bool IsCurrent(int version, CancellationToken token)
            => version == _searchVersion && !token.IsCancellationRequested;

        private bool HandleException(Exception exception, CancellationToken token)
        {
            switch (exception)
            {
                case OperationCanceledException when token.IsCancellationRequested:
                    return true;
                case TaskCanceledException:
                    ErrorMessage = "网络请求超时，请检查网络后重试";
                    break;
                case HttpRequestException:
                    ErrorMessage = $"搜索失败：{exception.Message}";
                    break;
                case BangumiApiException:
                    ErrorMessage = $"数据源请求失败：{exception.Message}";
                    break;
                case JsonException:
                    ErrorMessage = $"搜索结果解析失败：{exception.Message}";
                    break;
                default:
                    return false;
            }

            IsError = true;
            return true;
        }

        private void ClearError()
        {
            IsError = false;
            ErrorMessage = null;
        }
    }
}
