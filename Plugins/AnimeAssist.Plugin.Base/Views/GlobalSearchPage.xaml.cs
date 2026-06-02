using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System.Collections.ObjectModel;
using System.Text.Json;
using Windows.Foundation;
using Windows.UI;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class GlobalSearchPage : Page
    {
        private IAnimeDataSource? _dataSource;
        private string? _currentKeyword;
        private int _currentOffset;
        private int _totalResults;
        private const int PageSize = 20;

        // ======== 拖放 ========
        private DragDropService _dragDrop = null!;
        private HashSet<int> _blockedIds = new();

        public GlobalSearchPage()
        {
            InitializeComponent();
            _dragDrop = AppServices.Provider!.GetRequiredService<DragDropService>();
        }

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            RootGrid.AddHandler(UIElement.PointerPressedEvent,
                new PointerEventHandler(OnRootPointerPressed), true);

            // 提前加载屏蔽列表，确保后续搜索能正确过滤
            _ = LoadBlockedIdsAsync();
        }

        private async Task LoadBlockedIdsAsync()
        {
            var tracking = AppServices.Provider?.GetRequiredService<TrackingService>();
            if (tracking != null)
            {
                var blocked = await tracking.GetAnimeIdsByStatusAsync(AnimeTrackingStatus.Blocked);
                _blockedIds = blocked.ToHashSet();
            }
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
            if (_dataSource == null || _currentKeyword == null)
            {
                _dataSource = AppServices.Provider?.GetRequiredService<IAnimeDataSource>();
                if (_dataSource == null) return;
            }

            LoadingOverlay.Visibility = Visibility.Visible;
            LoadingRing.IsActive = true;
            PrevButton.IsEnabled = false;
            NextButton.IsEnabled = false;

            try
            {
                var (results, total) = await _dataSource.SearchByKeywordAsync(_currentKeyword, offset, CancellationToken.None);

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
            catch (Exception ex)
            {
                ResultCount.Text = $"搜索失败：{ex.Message}";
                PaginationBar.Visibility = Visibility.Collapsed;
            }
            finally
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                LoadingRing.IsActive = false;
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

        private void OnResultItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Anime anime)
                Frame.Navigate(typeof(AnimeDetailPage), anime.ID);
        }

        // ======== 拖放标记 ========

        private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _dragDrop.HandlePointerPressed(this, e);
        }

        private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            _dragDrop.HandlePointerMoved(this, DragOverlay, e, DragAction.Watching, DragAction.PlanToWatch);
        }

        private void OnRootPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _dragDrop.HandlePointerReleased(DragOverlay, e);
            CleanupOverlayAfterDrag();
        }

        private void OnRootPointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            _dragDrop.HandlePointerCanceled();
            CleanupOverlayAfterDrag();
        }

        private void CleanupOverlayAfterDrag()
        {
            if (!_dragDrop.IsDragging)
            {
                if (_dragDrop.DragGhost != null)
                    DragOverlay.Children.Remove(_dragDrop.DragGhost);
                foreach (var zone in _dragDrop.ActiveZones)
                    DragOverlay.Children.Remove(zone.Border);
                DragOverlay.Visibility = Visibility.Collapsed;
            }
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
