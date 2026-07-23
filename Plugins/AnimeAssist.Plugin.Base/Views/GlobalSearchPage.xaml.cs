using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Text.Json;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class GlobalSearchPage : Page
    {
        private IAnimeDataSource _dataSource;
        private DragDropService _dragDrop;
        private TrackingService _tracking;
        private IPluginNavigator _pluginNavigator;
        private string? _currentKeyword;
        private int _currentOffset;
        private int _totalResults;
        private const int PageSize = 20;
        private HashSet<int> _blockedIds = new();
        private CancellationTokenSource? _searchCts;
        private int _searchVersion;

        public GlobalSearchPage(DragDropService dragDropService, IAnimeDataSource dataSource, TrackingService trackingService, IPluginNavigator pluginNavigator)
        {
            InitializeComponent();
            _dragDrop = dragDropService;
            _dataSource = dataSource;
            _tracking = trackingService;
            _pluginNavigator = pluginNavigator;
        }

        private bool _dropHostRegistered;

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            EnsureDropHostRegistered(RootGrid);

            // 确保 Unloaded 只注册一次
            RootGrid.Unloaded -= OnRootGridUnloaded;
            RootGrid.Unloaded += OnRootGridUnloaded;

            // 提前加载屏蔽列表，确保后续搜索能正确过滤
            _ = LoadBlockedIdsAsync();
        }

        private void EnsureDropHostRegistered(Grid rootGrid)
        {
            if (_dropHostRegistered)
                return;
            _dropHostRegistered = true;

            _dragDrop.SetActiveDropContext(rootGrid, DragOverlay, DragAction.PlanToWatch);
            _dragDrop.RegisterStandardDragHost(rootGrid);
        }

        private void OnRootGridUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Grid rootGrid)
                return;

            _dragDrop.ClearActiveDropContext(rootGrid);
            _dragDrop.UnregisterStandardDragHost(rootGrid);
            _dropHostRegistered = false;
        }

        private async Task LoadBlockedIdsAsync()
        {
            try
            {
                var blocked = await _tracking.GetAnimeIdsByStatusAsync(AnimeTrackingStatus.Blocked);
                _blockedIds = blocked.ToHashSet();
            }
#pragma warning disable CA1031 // 屏蔽列表加载失败不影响搜索
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GlobalSearchPage] LoadBlockedIdsAsync failed: {ex.Message}");
            }
#pragma warning restore CA1031
        }

        private void OnSearchClick(object sender, RoutedEventArgs e)
        {
            StartSearch();
        }

        private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
                StartSearch();
        }

        private void StartSearch()
        {
            var keyword = SearchBox.Text?.Trim();
            if (string.IsNullOrEmpty(keyword)) return;

            _currentKeyword = keyword;
            _currentOffset = 0;
            _ = SearchAsync(0);
        }

        private async Task SearchAsync(int offset)
        {
            // 取消上一轮搜索，避免旧结果覆盖新结果
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = new CancellationTokenSource();
            var token = _searchCts.Token;
            var version = Interlocked.Increment(ref _searchVersion);

            LoadingOverlay.Visibility = Visibility.Visible;
            LoadingRing.IsActive = true;
            PrevButton.IsEnabled = false;
            NextButton.IsEnabled = false;

            try
            {
                var (results, total) = await _dataSource.SearchByKeywordAsync(_currentKeyword ?? string.Empty, offset, token);

                // 如果已有更新的搜索，丢弃此结果
                if (version != _searchVersion || token.IsCancellationRequested)
                    return;

                _currentOffset = offset;
                _totalResults = total;

                var filtered = results.Where(a => !_blockedIds.Contains(a.ID)).ToList();
                ResultGrid.ItemsSource = filtered;

                var currentPage = (offset / PageSize) + 1;
                var totalPages = Math.Max(1, (int)Math.Ceiling((double)total / PageSize));
                var totalDisplay = total >= 1000 ? $"{total}+" : total.ToString();
                ResultCount.Text = $"找到 {totalDisplay} 部番剧 · 第 {currentPage}/{totalPages} 页";
                PageInfo.Text = $"{currentPage} / {totalPages}";

                PrevButton.IsEnabled = offset > 0;
                NextButton.IsEnabled = (offset + PageSize) < total;
                PaginationBar.Visibility = Visibility.Visible;

                if (results.Count == 0)
                {
                    ResultCount.Text = "未找到相关番剧";
                    PaginationBar.Visibility = Visibility.Collapsed;
                }
            }
            catch (HttpRequestException ex)
            {
                if (version == _searchVersion)
                    ResultCount.Text = $"搜索失败：{ex.Message}";
            }
            catch (TaskCanceledException)
            {
                // 被新请求取消时静默返回，不更新 UI
            }
            catch (JsonException ex)
            {
                if (version == _searchVersion)
                {
                    ResultCount.Text = $"搜索结果解析失败：{ex.Message}";
                    PaginationBar.Visibility = Visibility.Collapsed;
                }
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

        private void OnPrevPage(object sender, RoutedEventArgs e)
        {
            var newOffset = _currentOffset - PageSize;
            if (newOffset >= 0)
                _ = SearchAsync(newOffset);
        }

        private void OnNextPage(object sender, RoutedEventArgs e)
        {
            var newOffset = _currentOffset + PageSize;
            if (newOffset < _totalResults)
                _ = SearchAsync(newOffset);
        }

        private void OnAnimeCardClicked(object? sender, Views.Controls.AnimeCardClickedEventArgs e)
        {
            _pluginNavigator.Navigate(typeof(AnimeDetailPage), e.Anime.ID);
        }

        // ======== 拖放标记 ========



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
