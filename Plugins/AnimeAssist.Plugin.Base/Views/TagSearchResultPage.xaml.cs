using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class TagSearchResultPage : Page, INavigationAware
    {
        private IAnimeDataSource? _dataSource;
        private CacheService? _cacheService;
        private IPluginNavigator _pluginNavigator;
        private string? _currentTag;
        private string _currentSort = "rank";
        private bool _sortDescending = true;
        private const int PageSize = 20;
        private const int YearStart = 1960;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };

        // 年份区间状态
        private int _yearFrom;
        private int _yearTo;

        // 全量数据（当前年份区间）
        private List<Anime>? _rawData;
        private List<Anime>? _allData;
        private int _currentOffset;

        private readonly ObservableCollection<Anime> _animeSource = new();

        private bool _isInitializing;

        // ======== 拖放标记 ========

        private DragDropService _dragDrop;
        private TrackingService _tracking;
        private HashSet<int> _blockedIds = new();
        private CancellationTokenSource? _searchCts;
        private int _searchVersion;

        public TagSearchResultPage(DragDropService dragDropService, IAnimeDataSource dataSource, CacheService? cacheService, TrackingService trackingService, IPluginNavigator pluginNavigator)
        {
            InitializeComponent();
            ResultGrid.ItemsSource = _animeSource;
            _dragDrop = dragDropService;
            _dataSource = dataSource;
            _cacheService = cacheService;
            _tracking = trackingService;
            _pluginNavigator = pluginNavigator;
        }

        private IDisposable? _dropHostRegistration;

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            _dropHostRegistration?.Dispose();
            _dropHostRegistration = _dragDrop.AttachStandardDragHost(
                RootGrid,
                DragOverlay,
                DragAction.PlanToWatch);

            // 确保 Unloaded 只注册一次
            RootGrid.Unloaded -= OnRootGridUnloaded;
            RootGrid.Unloaded += OnRootGridUnloaded;

            // 提前加载屏蔽列表，确保后续数据加载能正确过滤
            _ = LoadBlockedIdsAsync();
        }

        private void OnRootGridUnloaded(object sender, RoutedEventArgs e)
        {
            _dropHostRegistration?.Dispose();
            _dropHostRegistration = null;
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = null;
            Interlocked.Increment(ref _searchVersion);
        }

        private async Task LoadBlockedIdsAsync()
        {
            try
            {
                _blockedIds = await _tracking.GetBlockedAnimeIdsAsync();
                // 如果数据已经加载完成，重新过滤
                if (_rawData != null)
                {
                    ApplyPresentation();
                    ShowPage(0);
                }
            }
#pragma warning disable CA1031 // 屏蔽列表加载失败不影响已有数据
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TagSearchResultPage] LoadBlockedIdsAsync failed: {ex.Message}");
            }
#pragma warning restore CA1031
        }

        public async Task OnNavigatedToAsync(object? parameter)
        {
            if (parameter is string tagName && !string.IsNullOrEmpty(tagName))
            {
                TagTitle.Text = tagName;
                _currentTag = tagName;
                _currentSort = "rank";
                _sortDescending = true;
                SortOrderToggle.IsChecked = true;
                _currentOffset = 0;
                _rawData = null;
                _allData = null;
                _animeSource.Clear();

                _isInitializing = true;
                try
                {
                    InitYearComboBoxes();
                }
                finally
                {
                    _isInitializing = false;
                }
                await LoadYearRangeAsync();
            }
        }

        private void InitYearComboBoxes()
        {
            var currentYear = DateTime.Now.Year;
            YearFromCombo.Items.Clear();
            YearToCombo.Items.Clear();

            for (int y = currentYear; y >= YearStart; y--)
            {
                YearFromCombo.Items.Add(new ComboBoxItem { Content = y.ToString(), Tag = y });
                YearToCombo.Items.Add(new ComboBoxItem { Content = y.ToString(), Tag = y });
            }

            // 默认近 3 年
            _yearFrom = currentYear - 3;
            _yearTo = currentYear;

            foreach (var item in YearFromCombo.Items)
                if (item is ComboBoxItem ci && ci.Tag is int y && y == _yearFrom)
                    YearFromCombo.SelectedItem = item;
            foreach (var item in YearToCombo.Items)
                if (item is ComboBoxItem ci && ci.Tag is int y && y == _yearTo)
                    YearToCombo.SelectedItem = item;
        }

        private async Task LoadYearRangeAsync()
        {
            if (_dataSource == null || _currentTag == null) return;

            // 取消上一轮搜索
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = new CancellationTokenSource();
            var token = _searchCts.Token;
            var version = Interlocked.Increment(ref _searchVersion);

            var cacheKey = $"tag_search:{_currentTag}:{_yearFrom}:{_yearTo}";

            if (_cacheService != null)
            {
                var cached = await _cacheService.GetCacheAsync(cacheKey);
                token.ThrowIfCancellationRequested();
                if (version != _searchVersion) return;
                if (cached != null)
                {
                    try
                    {
                        var deserialized = JsonSerializer.Deserialize<List<Anime>>(
                            cached,
                            JsonOptions);
                        if (deserialized != null && deserialized.Count > 0)
                        {
                            _rawData = deserialized;
                            ApplyPresentation();
                            ShowPage(0);
                            return;
                        }
                    }
                    catch (JsonException)
                    {
                        await _cacheService.RemoveCacheAsync(cacheKey);
                    }
                }
            }

            if (version != _searchVersion) return;
            LoadingOverlay.Visibility = Visibility.Visible;
            LoadingRing.IsActive = true;
            PrevButton.IsEnabled = false;
            NextButton.IsEnabled = false;

            // 限制最大年份跨度为 10 年，防止大量串行 API 请求
            const int maxYearSpan = 10;
            if ((_yearTo - _yearFrom) > maxYearSpan)
            {
                if (version == _searchVersion)
                    ResultCount.Text = $"年份跨度超过 {maxYearSpan} 年，请缩小搜索范围";
                LoadingOverlay.Visibility = Visibility.Collapsed;
                LoadingRing.IsActive = false;
                return;
            }

            try
            {
                var allResults = new List<Anime>();
                var seenIds = new HashSet<int>();
                var yearCount = _yearTo - _yearFrom + 1;

                for (int y = _yearFrom; y <= _yearTo; y++)
                {
                    var idx = y - _yearFrom + 1;
                    LoadingText.Text = $"正在加载 {y} 年数据… 第 {idx}/{yearCount} 年";
                    await LoadYearDataAsync(y, allResults, seenIds, token);
                }

                token.ThrowIfCancellationRequested();
                if (version != _searchVersion) return;
                _rawData = allResults;
                ApplyPresentation();

                if (_cacheService != null && allResults.Count > 0)
                {
                    // 缓存原始搜索结果（不含屏蔽过滤），展示时再应用当前屏蔽列表
                    var json = JsonSerializer.Serialize(allResults, JsonOptions);
                    await _cacheService.SetCacheAsync(cacheKey, json, TimeSpan.FromHours(6));
                }

                ShowPage(0);
            }
            catch (HttpRequestException ex)
            {
                if (version == _searchVersion)
                    ResultCount.Text = $"搜索失败：{ex.Message}";
                PaginationBar.Visibility = Visibility.Collapsed;
            }
            catch (BangumiApiException ex)
            {
                if (version == _searchVersion)
                    ResultCount.Text = $"数据源请求失败：{ex.Message}";
                PaginationBar.Visibility = Visibility.Collapsed;
            }
            catch (OperationCanceledException)
            {
                // 被新请求取消时静默返回，不更新 UI
                PaginationBar.Visibility = Visibility.Collapsed;
            }
            catch (JsonException ex)
            {
                if (version == _searchVersion)
                    ResultCount.Text = $"搜索结果解析失败：{ex.Message}";
                PaginationBar.Visibility = Visibility.Collapsed;
            }
            finally
            {
                if (version == _searchVersion)
                {
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                    LoadingRing.IsActive = false;
                }
            }
        }

        private async Task LoadYearDataAsync(int year, List<Anime> results, HashSet<int> seenIds, CancellationToken cancellationToken)
        {
            if (_dataSource == null || _currentTag == null) return;

            var dateFrom = $"{year}-01-01";
            var dateTo = $"{year + 1}-01-01";

            for (int offset = 0; ;)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (data, _) = await _dataSource.SearchByTagAsync(_currentTag, offset, "match", cancellationToken,
                    airDateFrom: dateFrom, airDateTo: dateTo);

                if (data.Count == 0) break;

                foreach (var anime in data)
                    if (seenIds.Add(anime.ID))
                        results.Add(anime);

                if (data.Count < PageSize) break;
                offset += PageSize;
            }
        }

        private void ShowPage(int offset)
        {
            if (_allData == null || _allData.Count == 0)
            {
                _animeSource.Clear();
                ResultCount.Text = "未找到相关番剧";
                PaginationBar.Visibility = Visibility.Collapsed;
                return;
            }

            _currentOffset = offset;

            _animeSource.Clear();
            for (int i = offset; i < offset + PageSize && i < _allData.Count; i++)
                _animeSource.Add(_allData[i]);

            var currentPage = (offset / PageSize) + 1;
            var totalPages = Math.Max(1, (int)Math.Ceiling((double)_allData.Count / PageSize));
            ResultCount.Text = $"找到 {_allData.Count} 部番剧 · 第 {currentPage}/{totalPages} 页";
            PageInfo.Text = $"{currentPage} / {totalPages}";

            PrevButton.IsEnabled = offset > 0;
            NextButton.IsEnabled = (offset + PageSize) < _allData.Count;
            PaginationBar.Visibility = Visibility.Visible;
        }

        private void OnYearRangeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            if (YearFromCombo.SelectedItem == null || YearToCombo.SelectedItem == null) return;

            var from = (int)((ComboBoxItem)YearFromCombo.SelectedItem).Tag;
            var to = (int)((ComboBoxItem)YearToCombo.SelectedItem).Tag;

            if (from > to) return;

            _yearFrom = from;
            _yearTo = to;

            _currentOffset = 0;
            _animeSource.Clear();
            _ = LoadYearRangeAsync();
        }

        private void OnSortChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_allData == null) return;

            if (SortSelector.SelectedItem is ComboBoxItem item && item.Tag is string sort)
            {
                _currentSort = sort;
                _allData = SortLocally(_allData);
                ShowPage(0);
            }
        }

        private void OnSortOrderToggle(object sender, RoutedEventArgs e)
        {
            if (_allData == null) return;

            _sortDescending = SortOrderToggle.IsChecked == true;
            SortOrderToggle.Content = _sortDescending ? "↓" : "↑";
            _allData = SortLocally(_allData);
            ShowPage(0);
        }

        private List<Anime> SortLocally(List<Anime> items)
        {
            var sorted = _currentSort switch
            {
                "rank" => items.OrderBy(a => a.Score ?? 0),
                "date" => items.OrderBy(a => a.AirDate.HasValue ? 0 : 1)
                               .ThenBy(a => a.AirDate ?? DateOnly.MinValue),
                _ => (IOrderedEnumerable<Anime>)items.OrderBy(a => 0)
            };

            return _sortDescending
                ? sorted.Reverse().ToList()
                : sorted.ToList();
        }

        private void ApplyPresentation()
        {
            _allData = SortLocally(AnimeListPresentation.Filter(
                _rawData ?? [],
                _blockedIds).ToList());
        }

        private void OnPrevPage(object sender, RoutedEventArgs e)
        {
            var newOffset = _currentOffset - PageSize;
            if (newOffset >= 0)
                ShowPage(newOffset);
        }

        private void OnNextPage(object sender, RoutedEventArgs e)
        {
            var newOffset = _currentOffset + PageSize;
            if (newOffset < _allData?.Count)
                ShowPage(newOffset);
        }

        private void OnAnimeCardClicked(object? sender, Views.Controls.AnimeCardClickedEventArgs e)
        {
            _pluginNavigator.Navigate(typeof(AnimeDetailPage), e.Anime.ID);
        }



        private static List<T> FindAllElements<T>(DependencyObject parent) where T : DependencyObject
        {
            var r = new List<T>();
            FindAllRecursive(parent, r);
            return r;
        }

        private static void FindAllRecursive<T>(DependencyObject p, List<T> r) where T : DependencyObject
        {
            int c = VisualTreeHelper.GetChildrenCount(p);
            for (int i = 0; i < c; i++)
            {
                var ch = VisualTreeHelper.GetChild(p, i);
                if (ch is T t) r.Add(t);
                FindAllRecursive(ch, r);
            }
        }
    }
}
