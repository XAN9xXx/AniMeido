using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using System.Collections.ObjectModel;
using Windows.Foundation;
using Windows.UI;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AniMeido.Plugin.Base.Services
{
    /// <summary>
    /// 拖放标记服务：管理拖放状态、Ghost 卡片、Zone 交互。
    /// 各页面提供 overlay 容器和 Zone 排除规则，服务负责拖放核心逻辑。
    /// </summary>
    public sealed class DragDropService
    {
        private readonly TrackingService _tracking;

        // 拖放状态
        private Anime? _dragAnime;
        private bool _dragPointerDown;
        private Point _dragPointerDownPos;
        private Point _dragGhostOffset;
        private Border? _dragGhost;
        private bool _isDragging;

        // Zone 状态
        private List<DragZoneConfig> _dragZones = DragZoneConfig.GetDefaults();
        private readonly Dictionary<string, DragDropZoneInfo> _overlayZones = new();

        public DragDropService(TrackingService tracking)
        {
            _tracking = tracking;
        }

        /// <summary>是否正在拖放中。</summary>
        public bool IsDragging => _isDragging;

        /// <summary>当前拖动的番剧。</summary>
        public Anime? DragAnime => _dragAnime;

        // Ghost 和 Zone 的 UI 元素引用（供页面清理用）
        public Border? DragGhost => _dragGhost;
        public IReadOnlyCollection<DragDropZoneInfo> ActiveZones => _overlayZones.Values;

        /// <summary>重新加载拖放配置。</summary>
        public async Task ReloadConfigAsync()
        {
            _dragZones = await _tracking.LoadDragZoneConfigAsync();
        }

        /// <summary>
        /// 处理指针按下：检测是否在 AnimeCard 上，记录拖放起点。
        /// </summary>
        public void HandlePointerPressed(UIElement pageRoot, PointerRoutedEventArgs e)
        {
            if (_isDragging) return;
            _dragPointerDown = true;
            _dragPointerDownPos = e.GetCurrentPoint(pageRoot).Position;

            _dragAnime = null;
            var cards = FindAllElements<AnimeCard>(pageRoot);
            foreach (var card in cards)
            {
                var t = card.TransformToVisual(pageRoot);
                var o = t.TransformPoint(new Point(0, 0));
                var r = new Rect(o.X, o.Y, card.ActualWidth, card.ActualHeight);
                if (r.Contains(_dragPointerDownPos))
                {
                    _dragAnime = card.DataContext as Anime;
                    break;
                }
            }
        }

        /// <summary>
        /// 处理指针移动：跟踪 Ghost 位置，或触发拖放开始。
        /// </summary>
        /// <param name="excludeActions">Zone 中排除的 DragAction（页面自定义）。</param>
        /// <returns>拖放是否已开始。</returns>
        public bool HandlePointerMoved(UIElement pageRoot, UIElement overlay, PointerRoutedEventArgs e, params DragAction[] excludeActions)
        {
            if (_isDragging && _dragGhost != null)
            {
                var pt = e.GetCurrentPoint(overlay).Position;
                _dragGhost.Margin = new Thickness(pt.X + _dragGhostOffset.X, pt.Y + _dragGhostOffset.Y, 0, 0);
                return true;
            }

            if (!_dragPointerDown || _dragAnime == null) return false;
            var cp = e.GetCurrentPoint(pageRoot).Position;
            if (Math.Abs(cp.X - _dragPointerDownPos.X) < 8 &&
                Math.Abs(cp.Y - _dragPointerDownPos.Y) < 8) return false;

            _dragPointerDown = false;
            _ = BeginDragAsync(overlay, excludeActions);
            return true;
        }

        /// <summary>
        /// 处理指针释放：触发放置或取消。
        /// </summary>
        public void HandlePointerReleased(UIElement overlay, PointerRoutedEventArgs e)
        {
            _dragPointerDown = false;
            if (!_isDragging) return;
            EndDrag(e.GetCurrentPoint(overlay).Position);
        }

        /// <summary>
        /// 处理指针取消。
        /// </summary>
        public void HandlePointerCanceled()
        {
            _dragPointerDown = false;
            if (!_isDragging) return;
            CancelDrag();
        }

        private async Task BeginDragAsync(UIElement overlay, params DragAction[] excludeActions)
        {
            if (_isDragging) return;
            _isDragging = true;

            _dragZones = await _tracking.LoadDragZoneConfigAsync();

            overlay.Visibility = Visibility.Visible;
            overlay.UpdateLayout();
            BuildAndShowZones(overlay, excludeActions);

            var anime = _dragAnime!;
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
                cover.Source = new BitmapImage(ImageCacheHelper.GetImageUri(anime.ID, anime.CoverURL));
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
                TextTrimming = TextTrimming.CharacterEllipsis,
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
            if (overlay is Panel overlayPanel)
            {
                overlayPanel.Children.Add(ghost);
            }
            _dragGhost = ghost;
        }

        private void EndDrag(Point dropPoint)
        {
            ExecuteDrop(dropPoint);
            CleanupDrag();
        }

        private void CancelDrag() => CleanupDrag();

        private void CleanupDrag()
        {
            _isDragging = false;
            _dragAnime = null;
            _dragGhost = null;
            _overlayZones.Clear();
            _dragPointerDown = false;
        }

        /// <summary>
        /// 构建拖放 Zone（页面提供排除规则）。
        /// </summary>
        /// <param name="overlay">Zone 的容器元素。</param>
        /// <param name="excludeActions">需要排除的 DragAction 列表。</param>
        public void BuildAndShowZones(UIElement overlay, params DragAction[] excludeActions)
        {
            // 清除旧的 Zone（从 overlay 移除）
            foreach (var kv in _overlayZones)
            {
                if (overlay is Panel p) p.Children.Remove(kv.Value.Border);
            }
            _overlayZones.Clear();

            var pw = overlay is FrameworkElement fe ? fe.ActualWidth : 0;
            var ph = overlay is FrameworkElement fe2 ? fe2.ActualHeight : 0;
            var excludeSet = new HashSet<DragAction>(excludeActions);

            foreach (var config in _dragZones)
            {
                if (config.Action == DragAction.None || excludeSet.Contains(config.Action))
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

                if (overlay is Panel p) p.Children.Add(zone);
                _overlayZones[config.Id] = new DragDropZoneInfo(zone, inner, label);
            }
        }

        /// <summary>
        /// 当移除 zone 时调用（服务不再管理 overlay 的孩子，由页面清理）。
        /// </summary>
        public void ClearZonesFrom(UIElement overlay)
        {
            foreach (var kv in _overlayZones)
            {
                if (overlay is Panel p) p.Children.Remove(kv.Value.Border);
            }
            _overlayZones.Clear();
        }

        /// <summary>
        /// 获取拖放完成后的状态变更回调（页面可调用）。
        /// </summary>
        public static AnimeTrackingStatus DragActionToStatus(DragAction action) => action switch
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

        public static string GetActionLabel(DragAction action) => action switch
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

        /// <summary>
        /// Zone 的 DragOver 事件处理。
        /// </summary>
        public static void HandleZoneDragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            if (sender is Border zone && zone.Tag is string id)
            {
                // Zone 的可视反馈由页面自己的事件来处理
            }
        }

        /// <summary>
        /// 查找拖放完成时的目标 Zone 并执行标记。
        /// </summary>
        public void ExecuteDrop(Point dropPoint)
        {
            foreach (var kv in _overlayZones)
            {
                var z = kv.Value.Border;
                var zr = new Rect(z.Margin.Left, z.Margin.Top, z.ActualWidth, z.ActualHeight);
                if (zr.Contains(dropPoint))
                {
                    var cfg = _dragZones.Find(c => c.Id == kv.Key);
                    if (cfg != null && cfg.Action != DragAction.None && _dragAnime != null)
                    {
                        var st = DragActionToStatus(cfg.Action);
                        if (st != AnimeTrackingStatus.None)
                            _ = _tracking.SetStatusAsync(_dragAnime.ID, st);
                    }
                    break;
                }
            }
        }

        // ======== 视觉树辅助 ========

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

    /// <summary>
    /// 拖放 Zone 的 UI 元素记录。
    /// </summary>
    public record DragDropZoneInfo(
        Border Border,
        Border Inner,
        TextBlock Label);
}