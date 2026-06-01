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
        private TrackingService? _tracking;
        private List<DragZoneConfig> _dragZones = DragZoneConfig.GetDefaults();
        private readonly Dictionary<string, DragOverlayZone> _overlayZones = new();
        private Anime? _dragAnime;
        private bool _dragPointerDown;
        private Point _dragPointerDownPos;
        private Point _dragGhostOffset;
        private Border? _dragGhost;
        private bool _isDragging;

        public GlobalSearchPage()
        {
            InitializeComponent();
        }

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            RootGrid.AddHandler(UIElement.PointerPressedEvent,
                new PointerEventHandler(OnRootPointerPressed), true);
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

                ResultGrid.ItemsSource = results;

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

        private async Task BeginDragAsync(Anime anime)
        {
            if (_isDragging) return;
            _isDragging = true;

            _tracking ??= AppServices.Provider?.GetRequiredService<TrackingService>();
            if (_tracking != null)
                _dragZones = await _tracking.LoadDragZoneConfigAsync();

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
                cover.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                    ImageCacheHelper.GetImageUri(anime.ID, anime.CoverURL));
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
                if (config.Action == DragAction.None || config.Action == DragAction.Watching || config.Action == DragAction.PlanToWatch)
                    continue;

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
