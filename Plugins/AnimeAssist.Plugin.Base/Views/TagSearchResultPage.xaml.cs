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
    public sealed partial class TagSearchResultPage : Page
    {
        private IAnimeDataSource? _dataSource;
        private CacheService? _cacheService;
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
        private List<Anime>? _allData;
        private int _currentOffset;

        private readonly ObservableCollection<Anime> _animeSource = new();

        public TagSearchResultPage()
        {
            InitializeComponent();
            ResultGrid.ItemsSource = _animeSource;
        }

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            // 用 AddHandler 确保即使子元素处理了事件也能收到指针按下
            RootGrid.AddHandler(UIElement.PointerPressedEvent,
                new PointerEventHandler(OnRootPointerPressed), true);

            // 提前加载屏蔽列表，确保后续数据加载能正确过滤
            _ = LoadBlockedIdsAsync();
        }

        private async Task LoadBlockedIdsAsync()
        {
            var tracking = AppServices.Provider?.GetRequiredService<TrackingService>();
            if (tracking != null)
            {
                var blocked = await tracking.GetAnimeIdsByStatusAsync(AnimeTrackingStatus.Blocked);
                _blockedIds = blocked.ToHashSet();
                // 如果数据已经加载完成，重新过滤
                if (_allData != null && _allData.Count > 0)
                {
                    _allData = _allData.Where(a => !_blockedIds.Contains(a.ID)).ToList();
                    ShowPage(0);
                }
            }
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is string tagName && !string.IsNullOrEmpty(tagName))
            {
                TagTitle.Text = tagName;
                _currentTag = tagName;
                var sp = AppServices.Provider;
                _dataSource = sp?.GetRequiredService<IAnimeDataSource>();
                _cacheService = sp?.GetService<CacheService>();
                _currentSort = "rank";
                _sortDescending = true;
                SortOrderToggle.IsChecked = true;
                _currentOffset = 0;
                _animeSource.Clear();

                InitYearComboBoxes();
                await LoadYearRangeAsync();
            }
        }

        private void InitYearComboBoxes()
        {
            var currentYear = DateTime.Now.Year;

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

            var cacheKey = $"tag_search:{_currentTag}:{_yearFrom}:{_yearTo}";

            if (_cacheService != null)
            {
                var cached = await _cacheService.GetCacheAsync(cacheKey);
                if (cached != null)
                {
                    var deserialized = JsonSerializer.Deserialize<List<Anime>>(cached, JsonOptions);
                    if (deserialized != null && deserialized.Count > 0)
                    {
                        _allData = deserialized.Where(a => !_blockedIds.Contains(a.ID)).ToList();
                        _allData = SortLocally(_allData);
                        ShowPage(0);
                        return;
                    }
                }
            }

            LoadingOverlay.Visibility = Visibility.Visible;
            LoadingRing.IsActive = true;
            PrevButton.IsEnabled = false;
            NextButton.IsEnabled = false;

            try
            {
                var allResults = new List<Anime>();
                var seenIds = new HashSet<int>();
                var yearCount = _yearTo - _yearFrom + 1;

                for (int y = _yearFrom; y <= _yearTo; y++)
                {
                    var idx = y - _yearFrom + 1;
                    LoadingText.Text = $"正在加载 {y} 年数据… 第 {idx}/{yearCount} 年";
                    await LoadYearDataAsync(y, allResults, seenIds);
                }

                _allData = allResults.Where(a => !_blockedIds.Contains(a.ID)).ToList();

                if (_cacheService != null && _allData.Count > 0)
                {
                    var json = JsonSerializer.Serialize(_allData, JsonOptions);
                    await _cacheService.SetCacheAsync(cacheKey, json, TimeSpan.FromHours(6));
                }

                _allData = SortLocally(_allData);
                ShowPage(0);
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

        private async Task LoadYearDataAsync(int year, List<Anime> results, HashSet<int> seenIds)
        {
            if (_dataSource == null || _currentTag == null) return;

            var dateFrom = $"{year}-01-01";
            var dateTo = $"{year + 1}-01-01";

            for (int offset = 0; ;)
            {
                var (data, _) = await _dataSource.SearchByTagAsync(_currentTag, offset, "match", CancellationToken.None,
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

        private void OnResultItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Anime anime)
                Frame.Navigate(typeof(AnimeDetailPage), anime.ID);
        }

        // ======== 拖放标记（移植自 CurrentSeasonPage） ========

        private TrackingService? _tracking;
        private HashSet<int> _blockedIds = new();
        private List<DragZoneConfig> _dragZones = DragZoneConfig.GetDefaults();
        private readonly Dictionary<string, DragOverlayZone> _overlayZones = new();

        private Anime? _dragAnime;
        private bool _dragPointerDown;
        private Point _dragPointerDownPos;
        private Point _dragGhostOffset;
        private Border? _dragGhost;
        private bool _isDragging;

        private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isDragging && _dragGhost != null)
            {
                var pt = e.GetCurrentPoint(DragOverlay).Position;
                _dragGhost.Margin = new Thickness(pt.X + _dragGhostOffset.X, pt.Y + _dragGhostOffset.Y, 0, 0);
                return;
            }

            if (!_dragPointerDown || _dragAnime == null) return;
            var cp = e.GetCurrentPoint(this).Position;
            if (Math.Abs(cp.X - _dragPointerDownPos.X) < 8 &&
                Math.Abs(cp.Y - _dragPointerDownPos.Y) < 8) return;

            _dragPointerDown = false;
            _ = BeginDragAsync(_dragAnime);
        }

        private void OnRootPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _dragPointerDown = false;
            if (!_isDragging) return;
            EndDrag(e.GetCurrentPoint(DragOverlay).Position);
        }

        private void OnRootPointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            _dragPointerDown = false;
            if (!_isDragging) return;
            CancelDrag();
        }

        // 在 GridView 或 ScrollViewer 上监听指针按下，检测 AnimeCard
        private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_isDragging) return;
            _dragPointerDown = true;
            _dragPointerDownPos = e.GetCurrentPoint(this).Position;

            _dragAnime = null;
            var cards = FindAllElements<AnimeCard>(this);
            foreach (var card in cards)
            {
                var t = card.TransformToVisual(this);
                var o = t.TransformPoint(new Point(0, 0));
                var r = new Rect(o.X, o.Y, card.ActualWidth, card.ActualHeight);
                if (r.Contains(_dragPointerDownPos))
                {
                    _dragAnime = card.DataContext as Anime;
                    break;
                }
            }
        }

        private async Task BeginDragAsync(Anime anime)
        {
            if (_isDragging) return;
            _isDragging = true;

            _tracking ??= AppServices.Provider?.GetRequiredService<TrackingService>();
            if (_tracking != null)
            {
                _dragZones = await _tracking.LoadDragZoneConfigAsync();
                var blocked = await _tracking.GetAnimeIdsByStatusAsync(AnimeTrackingStatus.Blocked);
                _blockedIds = blocked.ToHashSet();
            }

            DragOverlay.Visibility = Visibility.Visible;
            DragOverlay.UpdateLayout();
            BuildAndShowZones();

            var ghost = new Border
            {
                Width = 150,
                Height = 256,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(200, 30, 30, 30)),
                Opacity = 0.85,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
            };

            var ghostGrid = new Grid();
            ghostGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(200) });
            ghostGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var cover = new Image
            {
                Stretch = Stretch.UniformToFill,
                Height = 200,
                VerticalAlignment = VerticalAlignment.Top,
            };
            if (!string.IsNullOrEmpty(anime.CoverURL))
            {
                cover.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                    ImageCacheHelper.GetImageUri(anime.ID, anime.CoverURL));
            }
            var clipRect = new Microsoft.UI.Xaml.Media.RectangleGeometry();
            clipRect.Rect = new Rect(0, 0, 150, 200);
            cover.Clip = clipRect;
            Grid.SetRow(cover, 0);
            ghostGrid.Children.Add(cover);

            var titleBlock = new TextBlock
            {
                Text = anime.Title ?? "",
                Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                Margin = new Thickness(8, 6, 8, 6),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            Grid.SetRow(titleBlock, 1);
            ghostGrid.Children.Add(titleBlock);

            ghost.Child = ghostGrid;

            _dragGhostOffset = new Point(-75, -128);
            ghost.Margin = new Thickness(
                _dragPointerDownPos.X + _dragGhostOffset.X,
                _dragPointerDownPos.Y + _dragGhostOffset.Y, 0, 0);
            DragOverlay.Children.Add(ghost);
            _dragGhost = ghost;
        }

        private void EndDrag(Point dropPoint)
        {
            foreach (var kv in _overlayZones)
            {
                var z = kv.Value.OuterBorder;
                var zr = new Rect(z.Margin.Left, z.Margin.Top, z.ActualWidth, z.ActualHeight);
                if (zr.Contains(dropPoint))
                {
                    var cfg = _dragZones.Find(c => c.Id == kv.Key);
                    if (cfg != null && cfg.Action != DragAction.None && _dragAnime != null)
                    {
                        var st = cfg.Action switch
                        {
                            DragAction.Watching => AnimeTrackingStatus.Watching,
                            DragAction.PlanToWatch => AnimeTrackingStatus.PlanToWatch,
                            DragAction.NotInterested => AnimeTrackingStatus.NotInterested,
                            DragAction.Following => AnimeTrackingStatus.Following,
                            DragAction.Completed => AnimeTrackingStatus.Completed,
                            DragAction.Dropped => AnimeTrackingStatus.Dropped,
                            DragAction.Blocked => AnimeTrackingStatus.Blocked,
                            _ => AnimeTrackingStatus.None
                        };
                        if (st != AnimeTrackingStatus.None)
                            _ = _tracking?.SetStatusAsync(_dragAnime.ID, st);
                    }
                    break;
                }
            }
            CleanupDrag();
        }

        private void CancelDrag() => CleanupDrag();

        private void CleanupDrag()
        {
            _isDragging = false;
            _dragAnime = null;
            if (_dragGhost != null) { DragOverlay.Children.Remove(_dragGhost); _dragGhost = null; }
            foreach (var kv in _overlayZones) DragOverlay.Children.Remove(kv.Value.OuterBorder);
            _overlayZones.Clear();
            DragOverlay.Visibility = Visibility.Collapsed;
        }

        private void BuildAndShowZones()
        {
            foreach (var kv in _overlayZones)
                DragOverlay.Children.Remove(kv.Value.OuterBorder);
            _overlayZones.Clear();

            var pw = DragOverlay.ActualWidth;
            var ph = DragOverlay.ActualHeight;

            foreach (var config in _dragZones)
            {
                // 搜索页排除追番、补番和禁用
                if (config.Action == DragAction.None || config.Action == DragAction.Watching || config.Action == DragAction.PlanToWatch) continue;

                var label = new TextBlock
                {
                    Text = GetActionLabel(config.Action),
                    FontSize = 16,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                };

                var inner = new Border
                {
                    Child = label,
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(16, 12, 16, 12),
                    Background = new SolidColorBrush(Color.FromArgb(180, 0x44, 0x88, 0xFF)),
                    Opacity = 0.7,
                };

                var zone = new Border
                {
                    Child = inner,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                    AllowDrop = true,
                    Tag = config.Id,
                };

                if (pw > 0 && ph > 0)
                {
                    zone.Width = pw * config.WidthPercent;
                    zone.Height = ph * config.HeightPercent;
                    zone.Margin = new Thickness(pw * config.XPercent, ph * config.YPercent, 0, 0);
                }

                DragOverlay.Children.Add(zone);
                _overlayZones[config.Id] = new DragOverlayZone(zone, inner, label);
            }
        }

        private static string GetActionLabel(DragAction action) => action switch
        {
            DragAction.Watching => "追番",
            DragAction.PlanToWatch => "补番",
            DragAction.NotInterested => "不感兴趣",
            DragAction.Following => "关注",
            DragAction.Completed => "已看完",
            DragAction.Dropped => "已弃番",
            DragAction.Blocked => "屏蔽",
            _ => "禁用"
        };

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
