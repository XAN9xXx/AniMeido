using System.Collections.ObjectModel;
using System.Text.Json;
using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

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
        private readonly PersonalFeatureStateStore _featureState;
        private readonly LocalSearchService _localSearch;
        private readonly SavedTagService _savedTags;
        private readonly ILogger _logger;
        private IReadOnlyList<Anime> _loaded = [];
        private IReadOnlyDictionary<int, AnimeTrackingStatus> _statuses = new Dictionary<int, AnimeTrackingStatus>();
        private int _total;
        private int _nextOffset;
        private CancellationTokenSource? _searchCancellation;
        private int _searchVersion;
        private int _landingVersion;
        private bool _localSearchPending;
        private bool _remoteSearchPending;

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
            PersonalFeatureStateStore featureState,
            LocalSearchService localSearch,
            SavedTagService savedTags,
            ILogger logger)
        {
            _dataSource = dataSource;
            _tracking = tracking;
            _featureState = featureState;
            _localSearch = localSearch;
            _savedTags = savedTags;
            _logger = logger;
            RecentSearches.CollectionChanged += (_, _) => OnLandingChanged();
            SavedTags.CollectionChanged += (_, _) => OnLandingChanged();
        }

        /// <summary>读取最近搜索与收藏的标签（空白首页用）。</summary>
        public async Task LoadLandingAsync()
        {
            var version = Interlocked.Increment(ref _landingVersion);
            try
            {
                var recent = await _featureState.LoadRecentSearchesAsync();
                if (version != _landingVersion)
                    return;

                Replace(RecentSearches, recent);
                var tags = await _savedTags.GetAllSavedTagsAsync();
                if (version != _landingVersion)
                    return;

                Replace(SavedTags, tags);
            }
            catch (SqliteException) when (version != _landingVersion)
            {
                // 旧读取已失效，失败也不能进入页面错误出口并覆盖当前状态。
            }
        }

        /// <summary>返回页面时重读追番状态，卡片标签与屏蔽随之更新。</summary>
        public async Task RefreshStatusesAsync()
        {
            var version = _searchVersion;
            // 读取期间提交了新搜索或回到首页，代次都会变化。
            if (!HasSearched || await TryReadStatusesAsync() is not { } statuses
                || version != _searchVersion)
                return;

            _statuses = statuses;
            for (var index = LocalMatches.Count - 1; index >= 0; index--)
            {
                var entry = LocalMatches[index];
                var status = _statuses.GetValueOrDefault(entry.Anime.ID);
                if (status is AnimeTrackingStatus.None or AnimeTrackingStatus.Blocked)
                    LocalMatches.RemoveAt(index);
                else if (status != entry.Status)
                    LocalMatches[index] = entry with { Status = status };
            }
            UpdateLocalSummary();

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
            IsLoadingMore = false;
            _localSearchPending = true;
            _remoteSearchPending = true;
            ClearError();
            _loaded = [];
            _total = 0;
            _nextOffset = 0;
            SelectedFormat = null;
            LocalMatches.Clear();
            UpdateLocalSummary();
            ApplyResults();
            await RememberAsync(trimmed);

            await RunPendingSearchAsync(version, token);
        }

        /// <summary>返回页面时只恢复尚未完成的搜索分支，保留已发布的结果和筛选。</summary>
        public Task ResumePendingSearchAsync()
        {
            if (!HasSearched || _searchCancellation is not null)
                return Task.CompletedTask;

            var version = BeginSearch(out var token);
            if (!_localSearchPending && !_remoteSearchPending)
            {
                UpdateCanLoadMore();
                return Task.CompletedTask;
            }

            IsSearching = true;
            ApplyResults();
            return RunPendingSearchAsync(version, token);
        }

        private async Task RunPendingSearchAsync(int version, CancellationToken token)
        {
            try
            {
                if (!IsCurrent(version, token))
                    return;
                var statuses = await TryReadStatusesAsync();
                if (!IsCurrent(version, token))
                    return;
                if (statuses is not null)
                    _statuses = statuses;

                // 两路独立发布，远程失败或仍在等待时也能显示已取得的本地匹配。
                await Task.WhenAll(
                    _localSearchPending ? PublishLocalMatchesAsync(Query, version, token) : Task.CompletedTask,
                    _remoteSearchPending ? SearchRemoteAsync(Query, version, token) : Task.CompletedTask);
            }
            catch (Exception ex) when (HandleException(ex, version, token))
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
                var (results, total, nextOffset) = await _dataSource.SearchByKeywordAsync(Query, _nextOffset, token);
                if (!IsCurrent(version, token))
                    return;

                _nextOffset = nextOffset;
                _loaded = SearchBrowse.AppendPage(_loaded, results);
                _total = total;
            }
            catch (Exception ex) when (HandleException(ex, version, token))
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
            _localSearchPending = false;
            _remoteSearchPending = false;
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
            var version = Interlocked.Increment(ref _landingVersion);
            await _featureState.SaveRecentSearchesAsync([]);
            if (version == _landingVersion)
                RecentSearches.Clear();
        }

        public void CancelPendingSearch()
        {
            Interlocked.Increment(ref _landingVersion);
            Interlocked.Increment(ref _searchVersion);
            _searchCancellation?.Cancel();
            _searchCancellation?.Dispose();
            _searchCancellation = null;
            IsSearching = false;
            IsLoadingMore = false;
            ApplyResults();
        }

        private void ApplyResults()
        {
            var visible = PastSeasonBrowse.Visible(_loaded, _statuses, null, hideCompleted: false);
            FormatChips = PastSeasonBrowse.FormatChips(visible, SelectedFormat);
            var shown = SearchBrowse.Sort(PastSeasonBrowse.ByFormat(visible, SelectedFormat), SortKey);
            if (!CollectionSync.SyncInPlace(Results, shown, entry => entry.Anime.ID))
                Replace(Results, shown);

            SummaryText = HasSearched && !_remoteSearchPending && !IsSearching
                ? SearchBrowse.Summary(_total, _loaded.Count) : "";
            UpdateCanLoadMore();
            HasNoResults = HasSearched && !_remoteSearchPending && !IsSearching && !IsError && Results.Count == 0;
            EmptyText = _loaded.Count == 0 ? $"没有找到与“{Query}”相关的番剧" : "当前筛选下没有作品";
        }

        private void UpdateCanLoadMore()
            => CanLoadMore = HasSearched && _searchCancellation is not null
                && !IsSearching && !_remoteSearchPending && _nextOffset < _total;

        private async Task PublishLocalMatchesAsync(string query, int version, CancellationToken token)
        {
            var local = await SearchLocalAsync(query, token);
            if (!IsCurrent(version, token))
                return;

            _localSearchPending = false;
            Replace(LocalMatches, local);
            UpdateLocalSummary();
        }

        private async Task SearchRemoteAsync(string query, int version, CancellationToken token)
        {
            try
            {
                var (results, total, nextOffset) = await _dataSource.SearchByKeywordAsync(query, 0, token);
                if (!IsCurrent(version, token))
                    return;

                _loaded = SearchBrowse.AppendPage([], results);
                _total = total;
                _nextOffset = nextOffset;
            }
            catch (Exception ex) when (HandleException(ex, version, token))
            {
                // 就地发布远程错误，不等待可能仍在读取详情的本地搜索。
            }
            finally
            {
                if (IsCurrent(version, token))
                {
                    _remoteSearchPending = false;
                    ApplyResults();
                }
            }
        }

        private void UpdateLocalSummary()
        {
            HasLocalMatches = LocalMatches.Count > 0;
            LocalSummary = $"{LocalMatches.Count} 部";
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
                or MetadataServiceException
                or SqliteException
                or InvalidOperationException
                or JsonException)
            {
                if (!token.IsCancellationRequested)
                    _logger.LogWarning(ex, "Local search results are unavailable; showing remote results only");
                return [];
            }
        }

        private async Task RememberAsync(string query)
        {
            var recent = SearchBrowse.PushRecent(RecentSearches.ToList(), query);
            Replace(RecentSearches, recent);
            try
            {
                await _featureState.SaveRecentSearchesAsync(recent);
            }
            catch (SqliteException ex)
            {
                _logger.LogWarning(ex, "Recent searches could not be saved; continuing the search");
            }
        }

        private async Task<IReadOnlyDictionary<int, AnimeTrackingStatus>?> TryReadStatusesAsync()
        {
            var version = _searchVersion;
            try
            {
                return (await _tracking.GetAllTrackingAsync()).ToDictionary(row => row.AnimeId, row => row.Status);
            }
            catch (SqliteException ex)
            {
                if (version == _searchVersion)
                    _logger.LogWarning(ex, "Tracking statuses could not be refreshed; keeping displayed statuses");
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
            Interlocked.Increment(ref _landingVersion);
            var version = Interlocked.Increment(ref _searchVersion);
            _searchCancellation?.Cancel();
            _searchCancellation?.Dispose();
            _searchCancellation = new CancellationTokenSource();
            token = _searchCancellation.Token;
            return version;
        }

        private bool IsCurrent(int version, CancellationToken token)
            => version == _searchVersion && !token.IsCancellationRequested;

        private bool HandleException(Exception exception, int version, CancellationToken token)
        {
            // 已离开页面或已提交新关键词时，旧请求的失败也不能覆盖新状态。
            if (!IsCurrent(version, token) && exception is OperationCanceledException
                or HttpRequestException or MetadataServiceException or JsonException)
                return true;

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
                case MetadataServiceException:
                    ErrorMessage = $"数据源请求失败：{exception.Message}";
                    break;
                case JsonException:
                    ErrorMessage = $"搜索结果解析失败：{exception.Message}";
                    break;
                default:
                    return false;
            }

            _logger.LogError(exception, "Anime search failed");
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
