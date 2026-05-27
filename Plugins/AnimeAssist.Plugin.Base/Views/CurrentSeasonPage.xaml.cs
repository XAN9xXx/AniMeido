using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class CurrentSeasonPage : Page
    {
        public CurrentSeasonViewModel ViewModel { get; }

        static bool _hasAutoScrolledOnce = false;
        private List<DragZoneConfig> _dragZones = DragZoneConfig.GetDefaults();
        private TrackingService? _tracking;

        public CurrentSeasonPage()
        {
            var ds = AppServices.Provider!.GetRequiredService<IAnimeDataSource>();
            ViewModel = new CurrentSeasonViewModel(ds);
            InitializeComponent();

            // 异步加载拖放配置
            _ = LoadDragConfigAsync();

            ViewModel.PropertyChanged += (s, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(CurrentSeasonViewModel.IsLoading):
                        UpdateOverlayState();
                        if (!ViewModel.IsLoading)
                            UpdateViewState();

                        // 首次打开时自动跳转到今天对应的星期分组
                        if (!_hasAutoScrolledOnce && !ViewModel.IsLoading && ViewModel.WeekdayGroups.Count > 0)
                        {
                            _hasAutoScrolledOnce = true;
                            int todayIndex = DateTime.Now.DayOfWeek switch
                            {
                                DayOfWeek.Sunday => 6,
                                _ => (int)DateTime.Now.DayOfWeek - 1
                            };
                            DelayedScrollToGroup(todayIndex);
                        }
                        break;

                    case nameof(CurrentSeasonViewModel.ErrorMessage):
                    case nameof(CurrentSeasonViewModel.IsError):
                        UpdateOverlayState();
                        UpdateViewState();
                        break;

                    case nameof(CurrentSeasonViewModel.HasData):
                        UpdateViewState();
                        break;
                }
            };

            ViewModel.LoadSeasonalAnimeCommand.Execute(null);
        }

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            var rootGrid = (Grid)sender;
            rootGrid.AddHandler(UIElement.PointerPressedEvent,
                new PointerEventHandler(OnCapturedPointerPressed), true);
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


        private async void DelayedScrollToGroup(int index)
        {
            for (int i = 0; i < 10; i++)
            {
                if (i > 0)
                    await Task.Delay(50);

                WeekdayRepeater.UpdateLayout();

                var container = WeekdayRepeater.ContainerFromIndex(index) as UIElement;
                if (container is not null)
                {
                    container.StartBringIntoView(new BringIntoViewOptions
                    {
                        AnimationDesired = true,
                        VerticalOffset = 0
                    });
                    await Task.Delay(500);
                    PlayBringIntoViewEffect(container);
                    return;
                }
            }
        }

        private void OnWeekdayItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Anime anime)
                Frame.Navigate(typeof(AnimeDetailPage), anime.ID);
        }

        private static void PlayBringIntoViewEffect(UIElement element)
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;

            visual.CenterPoint = new System.Numerics.Vector3(
                (float)element.ActualSize.X / 2,
                (float)element.ActualSize.Y / 2,
                0);

            var scaleX = compositor.CreateScalarKeyFrameAnimation();
            scaleX.InsertKeyFrame(0.0f, 1.0f);
            scaleX.InsertKeyFrame(0.6f, 0.97f);
            scaleX.InsertKeyFrame(0.8f, 1.01f);
            scaleX.InsertKeyFrame(1.0f, 1.0f);
            scaleX.Duration = TimeSpan.FromMilliseconds(800);

            var scaleY = compositor.CreateScalarKeyFrameAnimation();
            scaleY.InsertKeyFrame(0.0f, 1.0f);
            scaleY.InsertKeyFrame(0.6f, 0.97f);
            scaleY.InsertKeyFrame(0.8f, 1.01f);
            scaleY.InsertKeyFrame(1.0f, 1.0f);
            scaleY.Duration = TimeSpan.FromMilliseconds(800);

            visual.StartAnimation("Scale.X", scaleX);
            visual.StartAnimation("Scale.Y", scaleY);
        }

        // ======== 拖放标记（动态生成） ========

        private readonly Dictionary<string, DragOverlayZone> _overlayZones = new();

        private async Task LoadDragConfigAsync()
        {
            _tracking = AppServices.Provider!.GetRequiredService<TrackingService>();
            _dragZones = await _tracking.LoadDragZoneConfigAsync();
        }

        // ======== 自定义拖放 ========

        private Anime? _dragAnime;
        private bool _dragPointerDown;
        private Point _dragPointerDownPos;
        private Point _dragGhostOffset;
        private Border? _dragGhost;
        private bool _isDragging;

        private void OnCapturedPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_isDragging) return;
            _dragPointerDown = true;
            _dragPointerDownPos = e.GetCurrentPoint(this).Position;

            // 查找指针下方是否有 AnimeCard
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

            // 带封面图的卡片副本
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

        // ======== Zone 构建 ========

        private void BuildAndShowZones()
        {
            // 清除旧的动态 zone
            foreach (var kv in _overlayZones)
            {
                DragOverlay.Children.Remove(kv.Value.OuterBorder);
            }
            _overlayZones.Clear();

            var pw = DragOverlay.ActualWidth;
            var ph = DragOverlay.ActualHeight;

            foreach (var config in _dragZones)
            {
                // 追番页面不显示补番/禁用目标区
                if (config.Action == DragAction.None || config.Action == DragAction.PlanToWatch) continue;

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

                zone.DragOver += OnZoneDragOver;

                DragOverlay.Children.Add(zone);
                _overlayZones[config.Id] = new DragOverlayZone(zone, inner, label);
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
            _ => "禁用"
        };

        // ======== 辅助方法 ========

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

    // ======== 覆盖层 Zone 元素记录 ========

    internal record DragOverlayZone(
        Border OuterBorder,
        Border Inner,
        TextBlock Label);
}