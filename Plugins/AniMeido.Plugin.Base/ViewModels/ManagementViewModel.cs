using System.Collections.Concurrent;
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
    public record TagItem(string TagName, bool IsSelected);

    /// <summary>右侧显示的内容：某个状态、跨状态搜索结果或收藏的标签。</summary>
    public enum MineView
    {
        Status,
        Search,
        Tags,
    }

    /// <summary>批量或单部修改状态的结果。</summary>
    public readonly record struct StatusChangeResult(int Succeeded, int Failed);

    public partial class ManagementViewModel : ObservableObject
    {
        // 标签下只列前若干部，完整结果到标签页查看。
        private const int TagPreviewCount = 24;

        private readonly TrackingService _trackingService;
        private readonly IAnimeDataSource _animeDataSource;
        private readonly SavedTagService _savedTagService;
        private readonly LocalSearchService _searchService;
        private readonly ArchiveService _archiveService;
        // 作品详情跨状态、跨次加载复用：返回页面时内容不变，列表就不会重置滚动位置。
        private readonly ConcurrentDictionary<int, Anime> _animeCache = new();
        private Dictionary<int, (AnimeTrackingStatus Status, DateTimeOffset? MarkedAt)> _tracking = [];
        private IReadOnlyDictionary<int, double> _myRatings = new Dictionary<int, double>();
        private CancellationTokenSource? _viewCancellation;
        private int _viewVersion;

        public ObservableCollection<TrackingStatusSection> StatusSections { get; }

        public IReadOnlyList<TrackingStatusSection> MainSections { get; }

        public IReadOnlyList<TrackingStatusSection> HiddenSections { get; }

        /// <summary>当前列表（状态或搜索结果），原地同步以保留滚动位置。</summary>
        public ObservableCollection<MineEntry> Entries { get; } = [];

        /// <summary>所选标签下的作品。</summary>
        public ObservableCollection<PastSeasonEntry> TagEntries { get; } = [];

        public ObservableCollection<TagItem> TagList { get; } = [];

        [ObservableProperty]
        private TrackingStatusSection _selectedSection;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsStatusOrSearchView))]
        [NotifyPropertyChangedFor(nameof(IsTagsView))]
        private MineView _view = MineView.Status;

        [ObservableProperty]
        private bool _isHiddenExpanded;

        [ObservableProperty]
        private MineSortKey _sortKey = MineSortKey.RecentlyMarked;

        [ObservableProperty]
        private string _query = "";

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private bool _isError;

        [ObservableProperty]
        private string? _errorMessage;

        [ObservableProperty]
        private string _headerText = "";

        [ObservableProperty]
        private string _summaryText = "";

        [ObservableProperty]
        private bool _hasNoEntries;

        [ObservableProperty]
        private string _emptyText = "";

        [ObservableProperty]
        private int _tagCount;

        [ObservableProperty]
        private string? _selectedTag;

        [ObservableProperty]
        private bool _isTagLoading;

        [ObservableProperty]
        private string? _tagResultSummary;

        public bool IsStatusOrSearchView => View != MineView.Tags;

        public bool IsTagsView => View == MineView.Tags;

        public bool HasTags => TagList.Count > 0;

        public ManagementViewModel(
            TrackingService trackingService,
            IAnimeDataSource dataSource,
            SavedTagService savedTagService,
            LocalSearchService searchService,
            ArchiveService archiveService)
        {
            _trackingService = trackingService;
            _animeDataSource = dataSource;
            _savedTagService = savedTagService;
            _searchService = searchService;
            _archiveService = archiveService;
            StatusSections = new(TrackingStatusSection.CreateDefaults());
            MainSections = StatusSections.Where(section => !MineBrowse.IsHidden(section.Status)).ToList();
            HiddenSections = StatusSections.Where(section => MineBrowse.IsHidden(section.Status)).ToList();
            _selectedSection = StatusSections[0];
            _selectedSection.IsSelected = true;
            TagList.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTags));
        }

        /// <summary>
        /// 读取全部标记、个人评分与收藏的标签，再刷新当前内容（状态、搜索或标签）。
        /// 返回页面时也调用，因此只同步变化，不清空已显示的列表。
        /// </summary>
        public async Task LoadAsync(CancellationToken cancellationToken = default)
        {
            var version = BeginView(cancellationToken, out var token);
            IsLoading = true;
            ClearError();
            try
            {
                var rows = await _trackingService.GetAllTrackingAsync();
                var ratings = await TryReadRatingsAsync(token);
                var tags = await _savedTagService.GetAllSavedTagsAsync();
                if (!IsCurrent(version, token))
                    return;

                _tracking = rows.ToDictionary(
                    row => row.AnimeId,
                    row => (row.Status, MineBrowse.ParseMarkedAt(row.UpdatedAt)));
                _myRatings = ratings ?? _myRatings;
                ApplyCounts();
                SyncTagList(tags);

                await RefreshViewAsync(version, token);
            }
            catch (Exception ex) when (HandleLoadException(ex, token))
            {
            }
            finally
            {
                if (version == _viewVersion)
                    IsLoading = false;
            }
        }

        public async Task SelectSectionAsync(TrackingStatusSection section)
        {
            SelectSectionOnly(section);
            Query = "";
            View = MineView.Status;
            await RunViewAsync();
        }

        /// <summary>搜索词进入“待搜索”，由下一次 <see cref="LoadAsync"/> 执行（带搜索词打开页面时使用）。</summary>
        public void PrepareSearch(string query)
        {
            Query = query.Trim();
            View = Query.Length > 0 ? MineView.Search : MineView.Status;
        }

        public async Task SearchAsync(string query)
        {
            PrepareSearch(query);
            await RunViewAsync();
        }

        public async Task ClearSearchAsync()
        {
            if (View != MineView.Search)
                return;

            Query = "";
            View = MineView.Status;
            SelectSectionOnly(SelectedSection);
            await RunViewAsync();
        }

        public async Task ShowTagsAsync()
        {
            Query = "";
            View = MineView.Tags;
            foreach (var section in StatusSections)
                section.IsSelected = false;
            await RunViewAsync();
        }

        public void SetSortKey(MineSortKey key)
        {
            if (SortKey == key)
                return;

            SortKey = key;
            if (View != MineView.Tags)
                SyncEntries(MineBrowse.Sort(Entries.ToList(), key));
        }

        /// <summary>
        /// 把作品改到 <paramref name="target"/>（null 表示取消标记）。逐部写入，互不影响；
        /// 返回成功与失败的部数，列表与数量随即更新。
        /// </summary>
        public async Task<StatusChangeResult> ChangeStatusAsync(
            IReadOnlyList<MineEntry> entries,
            AnimeTrackingStatus? target)
        {
            var succeeded = 0;
            var failed = 0;
            foreach (var entry in entries)
            {
                try
                {
                    if (target is { } status)
                    {
                        if (_tracking.TryGetValue(entry.Anime.ID, out var current) && current.Status == status)
                            continue;

                        await _trackingService.SetStatusAsync(entry.Anime.ID, status);
                        _tracking[entry.Anime.ID] = (status, DateTimeOffset.UtcNow);
                    }
                    else
                    {
                        await _trackingService.RemoveStatusAsync(entry.Anime.ID);
                        _tracking.Remove(entry.Anime.ID);
                    }

                    succeeded++;
                }
                catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
                {
                    failed++;
                }
            }

            ApplyCounts();
            await RunViewAsync();
            return new StatusChangeResult(succeeded, failed);
        }

        public async Task SelectTagAsync(string tagName)
        {
            if (SelectedTag == tagName)
                return;

            SelectedTag = tagName;
            for (var index = 0; index < TagList.Count; index++)
            {
                var item = TagList[index];
                if (item.IsSelected != (item.TagName == tagName))
                    TagList[index] = item with { IsSelected = item.TagName == tagName };
            }

            await RunViewAsync();
        }

        public async Task RemoveTagAsync(string tagName)
        {
            await _savedTagService.RemoveTagAsync(tagName);
            var index = TagList.ToList().FindIndex(item => item.TagName == tagName);
            if (index >= 0)
                TagList.RemoveAt(index);

            TagCount = TagList.Count;
            if (SelectedTag == tagName)
            {
                SelectedTag = null;
                TagEntries.Clear();
                TagResultSummary = null;
            }

            if (View == MineView.Tags)
                await RunViewAsync();
        }

        public void CancelPendingLoads()
        {
            Interlocked.Increment(ref _viewVersion);
            _viewCancellation?.Cancel();
            _viewCancellation?.Dispose();
            _viewCancellation = null;
            IsLoading = false;
            IsTagLoading = false;
        }

        private void SelectSectionOnly(TrackingStatusSection section)
        {
            foreach (var item in StatusSections)
                item.IsSelected = ReferenceEquals(item, section);
            SelectedSection = section;
            if (MineBrowse.IsHidden(section.Status))
                IsHiddenExpanded = true;
        }

        private async Task RunViewAsync()
        {
            var version = BeginView(default, out var token);
            IsLoading = true;
            ClearError();
            try
            {
                await RefreshViewAsync(version, token);
            }
            catch (Exception ex) when (HandleLoadException(ex, token))
            {
            }
            finally
            {
                if (version == _viewVersion)
                    IsLoading = false;
            }
        }

        private async Task RefreshViewAsync(int version, CancellationToken token)
        {
            switch (View)
            {
                case MineView.Search:
                    await ShowSearchAsync(version, token);
                    break;
                case MineView.Tags:
                    await ShowTagAsync(version, token);
                    break;
                default:
                    await ShowSectionAsync(version, token);
                    break;
            }
        }

        private async Task ShowSectionAsync(int version, CancellationToken token)
        {
            var section = SelectedSection;
            var rows = _tracking
                .Where(pair => pair.Value.Status == section.Status)
                .Select(pair => (pair.Key, pair.Value.Status, pair.Value.MarkedAt))
                .ToList();
            await EnsureDetailsAsync(rows.Select(row => row.Key), token);
            if (!IsCurrent(version, token))
                return;

            SyncEntries(MineBrowse.Sort(Build(rows), SortKey));
            HeaderText = section.Label;
            EmptyText = section.EmptyMessage;
            HasNoEntries = Entries.Count == 0;
        }

        private async Task ShowSearchAsync(int version, CancellationToken token)
        {
            var query = Query;
            var results = await _searchService.SearchTrackedAsync(query, token);
            if (!IsCurrent(version, token))
                return;

            foreach (var result in results)
                _animeCache[result.Anime.ID] = result.Anime;
            var rows = results
                .Select(result => result.Anime.ID)
                .Where(_tracking.ContainsKey)
                .Select(id => (id, _tracking[id].Status, _tracking[id].MarkedAt))
                .ToList();

            SyncEntries(MineBrowse.Sort(Build(rows), SortKey));
            HeaderText = $"搜索“{query}”";
            EmptyText = "没有匹配的作品";
            HasNoEntries = Entries.Count == 0;
        }

        private async Task ShowTagAsync(int version, CancellationToken token)
        {
            HeaderText = "收藏的标签";
            if (SelectedTag is null && TagList.Count > 0)
            {
                SelectedTag = TagList[0].TagName;
                TagList[0] = TagList[0] with { IsSelected = true };
            }

            if (SelectedTag is not { } tag)
            {
                TagEntries.Clear();
                TagResultSummary = null;
                return;
            }

            IsTagLoading = true;
            try
            {
                var (results, total) = await _animeDataSource.SearchByTagAsync(tag, 0, "rank", token);
                if (!IsCurrent(version, token) || SelectedTag != tag)
                    return;

                var entries = results
                    .Select(anime => new PastSeasonEntry(
                        anime,
                        _tracking.TryGetValue(anime.ID, out var mark) ? mark.Status : AnimeTrackingStatus.None))
                    .Where(entry => entry.Status != AnimeTrackingStatus.Blocked)
                    .Take(TagPreviewCount)
                    .ToList();
                if (!CollectionSync.SyncInPlace(TagEntries, entries, entry => entry.Anime.ID))
                {
                    TagEntries.Clear();
                    foreach (var entry in entries)
                        TagEntries.Add(entry);
                }

                TagResultSummary = total > entries.Count
                    ? $"Bangumi 排名前 {entries.Count} 部，共 {total} 部"
                    : $"共 {entries.Count} 部";
            }
            finally
            {
                if (version == _viewVersion)
                    IsTagLoading = false;
            }
        }

        private IReadOnlyList<MineEntry> Build(
            IEnumerable<(int AnimeId, AnimeTrackingStatus Status, DateTimeOffset? MarkedAt)> rows)
            => MineBrowse.BuildEntries(
                rows,
                _animeCache,
                _myRatings,
                DateOnly.FromDateTime(DateTime.Now));

        private void SyncEntries(IReadOnlyList<MineEntry> target)
        {
            if (CollectionSync.SyncInPlace(Entries, target, entry => entry.Anime.ID))
                return;

            Entries.Clear();
            foreach (var entry in target)
                Entries.Add(entry);
        }

        private void ApplyCounts()
        {
            var counts = MineBrowse.CountByStatus(_tracking.Values.Select(value => value.Status));
            foreach (var section in StatusSections)
                section.Count = counts.GetValueOrDefault(section.Status);
            var shown = MainSections.Sum(section => section.Count);
            SummaryText = $"共 {shown} 部";
        }

        private void SyncTagList(IReadOnlyList<string> tags)
        {
            if (SelectedTag is { } selected && !tags.Contains(selected))
                SelectedTag = null;

            var wanted = tags.Select(tag => new TagItem(tag, tag == SelectedTag)).ToList();
            if (!CollectionSync.SyncInPlace(TagList, wanted, item => item.TagName))
            {
                TagList.Clear();
                foreach (var item in wanted)
                    TagList.Add(item);
            }

            TagCount = TagList.Count;
        }

        private async Task<IReadOnlyDictionary<int, double>?> TryReadRatingsAsync(CancellationToken token)
        {
            try
            {
                return await _archiveService.GetPersonalRatingsAsync(token);
            }
            catch (SqliteException)
            {
                // 个人评分只是附加信息，读取失败时列表照常显示。
                return null;
            }
        }

        /// <summary>补齐缺少的作品详情，已缓存的不再请求；单部失败时跳过该部。</summary>
        private async Task EnsureDetailsAsync(IEnumerable<int> ids, CancellationToken cancellationToken)
        {
            var missing = ids.Where(id => !_animeCache.ContainsKey(id)).Distinct().ToList();
            if (missing.Count == 0)
                return;

            await Parallel.ForEachAsync(
                missing,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = 4,
                    CancellationToken = cancellationToken,
                },
                async (id, token) =>
                {
                    try
                    {
                        var anime = await _animeDataSource.GetAnimeDetailAsync(id, token);
                        if (anime is not null)
                            _animeCache[id] = anime;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is HttpRequestException
                        or BangumiApiException
                        or InvalidOperationException
                        or JsonException)
                    {
                    }
                });
        }

        private int BeginView(CancellationToken outer, out CancellationToken token)
        {
            _viewCancellation?.Cancel();
            _viewCancellation?.Dispose();
            _viewCancellation = CancellationTokenSource.CreateLinkedTokenSource(outer);
            token = _viewCancellation.Token;
            return Interlocked.Increment(ref _viewVersion);
        }

        private bool IsCurrent(int version, CancellationToken token)
            => version == _viewVersion && !token.IsCancellationRequested;

        private bool HandleLoadException(
            Exception exception,
            CancellationToken cancellationToken)
        {
            switch (exception)
            {
                case OperationCanceledException
                    when cancellationToken.IsCancellationRequested:
                    return true;
                case TaskCanceledException:
                    ErrorMessage = "网络请求超时，请检查网络后重试";
                    break;
                case HttpRequestException:
                    ErrorMessage = $"网络请求失败：{exception.Message}";
                    break;
                case BangumiApiException:
                    ErrorMessage = $"数据源请求失败：{exception.Message}";
                    break;
                case SqliteException:
                    ErrorMessage = $"读取本地记录失败：{exception.Message}";
                    break;
                case InvalidOperationException:
                case JsonException:
                    ErrorMessage = $"数据解析失败：{exception.Message}";
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
