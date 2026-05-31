using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class PastSeasonPage : Page
    {
        private readonly PastSeasonViewModel _viewModel;
        public PastSeasonViewModel ViewModel => _viewModel;
        private List<DragZoneConfig> _dragZones = DragZoneConfig.GetDefaults();
        private TrackingService? _tracking;

        public PastSeasonPage()
        {
            var ds = AppServices.Provider!.GetRequiredService<IAnimeDataSource>();
            _viewModel = new PastSeasonViewModel(ds);
            InitializeComponent();

            // 异步加载拖放配置
            _ = LoadDragConfigAsync();

            ViewModel.PropertyChanged += (s, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(PastSeasonViewModel.IsLoading):
                        UpdateOverlayState();
                        if (!ViewModel.IsLoading)
                            UpdateViewState();
                        break;

                    case nameof(PastSeasonViewModel.ErrorMessage):
                    case nameof(PastSeasonViewModel.IsError):
                        UpdateOverlayState();
                        UpdateViewState();
                        break;

                    case nameof(PastSeasonViewModel.HasData):
                        UpdateViewState();
                        break;

                    case nameof(PastSeasonViewModel.TotalCount):
                        if (ViewModel.TotalCount > 0)
                        {
                            StatsCard.Visibility = Visibility.Visible;
                            TotalCountText.Text = ViewModel.TotalCount.ToString();
                        }
                        else
                        {
                            StatsCard.Visibility = Visibility.Collapsed;
                        }
                        break;
                }
            };

            InitializeComboBoxes();
        }

        private void UpdateViewState()
        {
            if (ViewModel.IsError)
            {
                ErrorInfoBar.Message = ViewModel.ErrorMessage;
                ErrorInfoBar.IsOpen = true;
                ErrorInfoBar.Visibility = Visibility.Visible;
                EmptyState.Visibility = Visibility.Collapsed;
            }
            else
            {
                ErrorInfoBar.IsOpen = false;
                ErrorInfoBar.Visibility = Visibility.Collapsed;
                EmptyState.Visibility = !ViewModel.IsLoading && !ViewModel.HasData
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        private void UpdateOverlayState()
        {
            bool showOverlay = ViewModel.IsLoading || ViewModel.IsError;
            LoadingOverlay.Visibility = showOverlay ? Visibility.Visible : Visibility.Collapsed;
            LoadingRing.IsActive = ViewModel.IsLoading;

            if (ViewModel.IsError)
            {
                LoadingFailedImage.Visibility = Visibility.Visible;
                LoadingRing.Visibility = Visibility.Collapsed;
                LoadingHint.Text = $"{ViewModel.ErrorMessage}\n\n点击重试";
            }
            else if (ViewModel.IsLoading)
            {
                LoadingFailedImage.Visibility = Visibility.Collapsed;
                LoadingRing.Visibility = Visibility.Visible;
                LoadingHint.Text = "加载中…";
            }
            else
            {
                LoadingFailedImage.Visibility = Visibility.Collapsed;
                LoadingHint.Text = "";
            }
        }

        private void OnLoadingOverlayTapped(object sender, TappedRoutedEventArgs e)
        {
            if (ViewModel.IsError)
            {
                ViewModel.RetryLoadCommand.Execute(null);
            }
        }

        private void InitializeComboBoxes()
        {
            int currentYear = DateTime.Now.Year;
            for (int y = 2000; y <= currentYear; y++)
                YearComboBox.Items.Add(y);
            YearComboBox.SelectedItem = currentYear;

            RebuildSeasonItems(currentYear);

            YearComboBox.SelectionChanged += OnYearSelectionChanged;
            SeasonComboBox.SelectionChanged += OnSeasonSelectionChanged;

            // 触发初始加载
            if (YearComboBox.SelectedItem is int year &&
                SeasonComboBox.SelectedItem is ComboBoxItem item && item.Tag is Season season)
            {
                _ = ViewModel.LoadPastSeasonAnimeAsync(year, season);
            }
        }

        private void RebuildSeasonItems(int year)
        {
            SeasonComboBox.SelectionChanged -= OnSeasonSelectionChanged;
            SeasonComboBox.Items.Clear();

            var allSeasons = new[] { Season.Winter, Season.Spring, Season.Summer, Season.Fall };
            var validSeasons = year < DateTime.Now.Year
                ? allSeasons
                : allSeasons.TakeWhile(s => s <= GetCurrentSeason()).ToArray();

            foreach (var season in validSeasons)
            {
                SeasonComboBox.Items.Add(new ComboBoxItem
                {
                    Content = season switch
                    {
                        Season.Winter => "冬 (1-3月)",
                        Season.Spring => "春 (4-6月)",
                        Season.Summer => "夏 (7-9月)",
                        Season.Fall => "秋 (10-12月)",
                        _ => season.ToString()
                    },
                    Tag = season
                });
            }

            // 选中当前季度（如果可用），否则选中最后一个
            var currentSeason = GetCurrentSeason();
            for (int i = 0; i < SeasonComboBox.Items.Count; i++)
            {
                if (((ComboBoxItem)SeasonComboBox.Items[i]).Tag is Season s && s == currentSeason)
                {
                    SeasonComboBox.SelectedIndex = i;
                    SeasonComboBox.SelectionChanged += OnSeasonSelectionChanged;
                    return;
                }
            }
            SeasonComboBox.SelectedIndex = SeasonComboBox.Items.Count - 1;
            SeasonComboBox.SelectionChanged += OnSeasonSelectionChanged;
        }

        private static Season GetCurrentSeason()
        {
            return DateTime.Now.Month switch
            {
                >= 1 and <= 3 => Season.Winter,
                >= 4 and <= 6 => Season.Spring,
                >= 7 and <= 9 => Season.Summer,
                _ => Season.Fall
            };
        }

        private void OnYearSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (YearComboBox.SelectedItem is not int year) return;
            RebuildSeasonItems(year);

            // 年份变更后立即加载新季度数据
            if (SeasonComboBox.SelectedItem is ComboBoxItem item && item.Tag is Season season)
            {
                _ = ViewModel.LoadPastSeasonAnimeAsync(year, season);
            }
        }

        private async void OnSeasonSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (YearComboBox.SelectedItem is not int year) return;
            if (SeasonComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not Season season) return;
            await ViewModel.LoadPastSeasonAnimeAsync(year, season);
        }

        private void OnItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Anime anime)
                Frame.Navigate(typeof(AnimeDetailPage), anime.ID);
        }

        private async Task LoadDragConfigAsync()
        {
            _tracking = AppServices.Provider!.GetRequiredService<TrackingService>();
            _dragZones = await _tracking.LoadDragZoneConfigAsync();
        }

        // ======== 自定义拖放 ========

        private readonly Dictionary<string, PastSeasonDragZone> _overlayZones = new();
        private Anime? _dragAnime;
        private bool _dragPointerDown;
        private Point _dragPointerDownPos;
        private Point _dragGhostOffset;
        private Border? _dragGhost;
        private bool _isDragging;

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            var rootGrid = (Grid)sender;
            rootGrid.AddHandler(UIElement.PointerPressedEvent,
                new PointerEventHandler(OnCapturedPointerPressed), true);
        }

        private void OnCapturedPointerPressed(object sender, PointerRoutedEventArgs e)
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

            if (_tracking == null)
                _tracking = AppServices.Provider!.GetRequiredService<TrackingService>();
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
                Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
                Height = 200,
                VerticalAlignment = VerticalAlignment.Top,
            };
            if (!string.IsNullOrEmpty(anime.CoverURL))
            {
                cover.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(anime.CoverURL));
            }
            Grid.SetRow(cover, 0);
            ghostGrid.Children.Add(cover);

            var titleBlock = new TextBlock
            {
                Text = anime.Title ?? "Anime",
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

            var clipRect = new RectangleGeometry();
            clipRect.Rect = new Rect(0, 0, 150, 200);
            cover.Clip = clipRect;

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

        private void BuildAndShowZones()
        {
            foreach (var kv in _overlayZones)
                DragOverlay.Children.Remove(kv.Value.OuterBorder);
            _overlayZones.Clear();

            var pw = DragOverlay.ActualWidth;
            var ph = DragOverlay.ActualHeight;

            foreach (var config in _dragZones)
            {
                // 补番页面不显示追番/禁用目标区
                if (config.Action == DragAction.None || config.Action == DragAction.Watching) continue;

                var label = new TextBlock
                {
                    Text = GetActionLabel(config.Action),
                    FontSize = 16,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                };

                var accent = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
                var inner = new Border
                {
                    Child = label,
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(16, 12, 16, 12),
                    Background = new SolidColorBrush(Color.FromArgb(180, accent.R, accent.G, accent.B)),
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
                _overlayZones[config.Id] = new PastSeasonDragZone(zone, inner, label);
            }
        }

        private void OnZoneDragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            if (sender is Border zone && zone.Tag is string id
                && _overlayZones.TryGetValue(id, out var dz))
            {
                dz.Inner.Visibility = Visibility.Visible;
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

        private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed) return typed;
                var result = FindChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }
    }

    internal record PastSeasonDragZone(
        Border OuterBorder,
        Border Inner,
        TextBlock Label);
}