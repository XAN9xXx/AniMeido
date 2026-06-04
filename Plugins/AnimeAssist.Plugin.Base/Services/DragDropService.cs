using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.UI;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Numerics;

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
        private Visual? _ghostVisual;  // Composition Visual for ghost positioning
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
        /// 处理指针按下：从 OriginalSource 向上查找 AnimeCard，记录拖放起点。
        /// </summary>
        public void HandlePointerPressed(UIElement pageRoot, PointerRoutedEventArgs e)
        {
            if (_isDragging) return;
            _dragPointerDown = true;
            _dragPointerDownPos = e.GetCurrentPoint(pageRoot).Position;

            _dragAnime = null;
            // 从原始事件源向上遍历视觉树，查找 AnimeCard（O(depth)而非 O(cards×depth)）
            var element = e.OriginalSource as DependencyObject;
            while (element != null)
            {
                if (element is AnimeCard card)
                {
                    _dragAnime = card.DataContext as Anime;
                    break;
                }
                element = VisualTreeHelper.GetParent(element);
            }
        }

        /// <summary>
        /// 处理指针移动：通过 Composition Offset 更新 Ghost 位置（合成线程，零布局开销）。
        /// </summary>
        public bool HandlePointerMoved(UIElement pageRoot, UIElement overlay, PointerRoutedEventArgs e, params DragAction[] excludeActions)
        {
            if (_isDragging && _ghostVisual != null)
            {
                var pt = e.GetCurrentPoint(overlay).Position;
                _ghostVisual.Offset = new Vector3(
                    (float)(pt.X + _dragGhostOffset.X),
                    (float)(pt.Y + _dragGhostOffset.Y),
                    0);
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
            EndDrag(overlay, e.GetCurrentPoint(overlay).Position);
        }

        /// <summary>
        /// 处理指针取消。
        /// </summary>
        public void HandlePointerCanceled(UIElement overlay)
        {
            _dragPointerDown = false;
            if (!_isDragging) return;
            CancelDrag(overlay);
        }

        private async Task BeginDragAsync(UIElement overlay, params DragAction[] excludeActions)
        {
            if (_isDragging) return;
            _isDragging = true;

            try
            {
                overlay.Visibility = Visibility.Visible;
                overlay.UpdateLayout(); // 确保 overlay 布局完成，否则 ActualWidth/Height 为 0 → Zone 挤在左上角
                BuildAndShowZones(overlay, excludeActions);

                var anime = _dragAnime!;

                // 创建轻量 Ghost：仅 Border + Image（去掉不可读的 TextBlock 降低合成负担）
                var coverImage = new Image
                {
                    Stretch = Stretch.UniformToFill,
                    Height = 200,
                    Width = 150,
                    VerticalAlignment = VerticalAlignment.Top,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                if (!string.IsNullOrEmpty(anime.CoverURL))
                {
                    var bmp = new BitmapImage();
                    bmp.DecodePixelWidth = 300; // 指定解码尺寸，避免全分辨率解码
                    bmp.UriSource = ImageCacheHelper.GetImageUri(anime.ID, anime.CoverURL);
                    coverImage.Source = bmp;
                }
                var clipRect = new RectangleGeometry();
                clipRect.Rect = new Rect(0, 0, 150, 200);
                coverImage.Clip = clipRect;

                var ghost = new Border
                {
                    Width = 150,
                    Height = 200,
                    CornerRadius = new CornerRadius(8),
                    Background = new SolidColorBrush(Color.FromArgb(200, 30, 30, 30)),
                    Opacity = 0.85,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    IsHitTestVisible = false,
                    Child = coverImage,
                };

                _dragGhostOffset = new Point(-75, -100);
                var initialX = (float)(_dragPointerDownPos.X + _dragGhostOffset.X);
                var initialY = (float)(_dragPointerDownPos.Y + _dragGhostOffset.Y);

                if (overlay is Panel overlayPanel)
                {
                    overlayPanel.Children.Add(ghost);
                }
                _dragGhost = ghost;

                // 用 Composition Offset 替代 Margin 定位——合成线程执行，不触发 UI 线程布局
                _ghostVisual = ElementCompositionPreview.GetElementVisual(ghost);
                _ghostVisual.Offset = new Vector3(initialX, initialY, 0);
            }
#pragma warning disable CA1031 // 拖放初始化失败应清理 UI 而非崩溃
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DragDrop] BeginDragAsync failed: {ex.Message}");
                _isDragging = false;
                CleanupDrag(overlay);
            }
#pragma warning restore CA1031
        }

        private void EndDrag(UIElement overlay, Point dropPoint)
        {
            ExecuteDrop(dropPoint);
            CleanupDrag(overlay);
        }

        private void CancelDrag(UIElement overlay) => CleanupDrag(overlay);

        /// <summary>
        /// 传入 overlay 以便从视觉树中移除 Ghost 和 Zone。
        /// 如果 overlay 不可用，UI 元素可能残留。
        /// </summary>
        public void CleanupDrag(UIElement? overlay = null)
        {
            if (overlay is Panel panel)
            {
                // 移除 ghost
                if (_dragGhost != null && panel.Children.Contains(_dragGhost))
                {
                    panel.Children.Remove(_dragGhost);
                }
                // 移除 zone
                foreach (var kv in _overlayZones)
                {
                    if (panel.Children.Contains(kv.Value.Border))
                        panel.Children.Remove(kv.Value.Border);
                }
            }

            _isDragging = false;
            _dragAnime = null;
            _dragGhost = null;
            _ghostVisual = null;
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
                    // Canvas 用附加属性定位，不触发 measure/arrange，避免拖放时布局干扰
                    Canvas.SetLeft(zone, pw * config.XPercent);
                    Canvas.SetTop(zone, ph * config.YPercent);
                }

                if (overlay is Canvas canvas) canvas.Children.Add(zone);
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
                // Zone 使用 Canvas.SetLeft/Top 定位，命中判断必须读取 Canvas 属性而非 Margin
                var zx = Canvas.GetLeft(z);
                var zy = Canvas.GetTop(z);
                var zr = new Rect(zx, zy, z.ActualWidth, z.ActualHeight);
                if (zr.Contains(dropPoint))
                {
                    var cfg = _dragZones.Find(c => c.Id == kv.Key);
                    if (cfg != null && cfg.Action != DragAction.None && _dragAnime != null)
                    {
                        var st = DragActionToStatus(cfg.Action);
                        if (st != AnimeTrackingStatus.None)
                            _ = SetStatusSafelyAsync(_dragAnime.ID, st);
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

        /// <summary>
        /// 安全地设置标记状态，捕获并记录异常。
        /// fire-and-forget，不阻塞拖放操作。
        /// </summary>
        private async Task SetStatusSafelyAsync(int animeId, AnimeTrackingStatus status)
        {
            try
            {
                await _tracking.SetStatusAsync(animeId, status);
            }
#pragma warning disable CA1031 // 拖放状态写入失败不阻塞操作
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DragDrop] Failed to set tracking status. AnimeId={animeId}, Status={status}: {ex.Message}");
            }
#pragma warning restore CA1031
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