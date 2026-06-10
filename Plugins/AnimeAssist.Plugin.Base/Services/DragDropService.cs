using AniMeido.Contracts.DragDrop;
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
    /// 拖放标记服务：管理 Zone 构建、标记状态路由、标准拖放协调与旧内部拖拽路径兼容。
    ///
    /// == 拖拽系统说明 ==
    ///
    /// [数据事实]
    ///   AnimeCardDragPayload — 跨窗口/跨区域的统一拖拽数据格式。
    ///   所有拖拽事件的数据来源均为 AnimeCardDragPayload JSON（StandardDataFormats.Text）。
    ///
    /// [Drag Source]
    ///   AnimeCard 本体标准拖拽（CanDrag=True, OnBodyDragStarting）— 当前主路径。
    ///   ShareHandle 辅助拖拽手柄 — 跨窗口拖拽辅助入口，payload 格式与主路径一致。
    ///
    /// [主窗口拖拽接收]
    ///   AnimeCardDropHost (Shell 级 fallback) → DragDropService.HandleStandardDragOver/DropAsync
    ///   → BuildAndShowZones → zone DragOver/Drop 事件 → TrackingService.SetStatusAsync
    ///
    /// [跨窗口拖拽接收 (ChatWindow)]
    ///   ChatWindow InputPanel AddHandler DragOver/Drop → AnimeCardDragPayloadSerializer.Deserialize
    ///   → ShowPendingCard → 用户发送 → 文字消息
    ///
    /// [旧内部拖拽路径 (Legacy)]
    ///   HandlePointerPressed/Moved/Released — 基于指针坐标的内部拖拽路径。
    ///   当前 AnimeCard（CanDrag=True）已跳过此路径。保留供未启用标准拖拽的页面兼容。
    /// </summary>
    public sealed class DragDropService
    {
        // 高频拖拽日志开关——Debug 模式下关闭以避免卡顿
        private const bool VerboseDragLog = false;

        private readonly TrackingService _tracking;

        // 拖放状态
        private Anime? _dragAnime;
        private AnimeCardDragPayload? _dragPayload;
        private bool _dragPointerDown;
        private Point _dragPointerDownPos;
        private Point _dragGhostOffset;
        private Border? _dragGhost;
        private Visual? _ghostVisual;  // Composition Visual for ghost positioning
        private bool _isDragging;

        // Zone 状态
        private List<DragZoneConfig> _dragZones = DragZoneConfig.GetDefaults();
        private readonly Dictionary<string, DragDropZoneInfo> _overlayZones = new();

        // 标准拖拽（ActiveDropContext）状态
        private UIElement? _standardPageRoot;
        private Panel? _standardOverlay;
        private DragAction[] _standardExcludeActions = Array.Empty<DragAction>();
        private string? _standardCurrentZoneId;



        public DragDropService(TrackingService tracking)
        {
            _tracking = tracking;
        }

        /// <summary>是否正在拖放中。</summary>
        public bool IsDragging => _isDragging;

        /// <summary>当前拖动的番剧。</summary>
        public Anime? DragAnime => _dragAnime;

        /// <summary>当前拖动的统一载荷。</summary>
        public AnimeCardDragPayload? DragPayload => _dragPayload;

        // Ghost 和 Zone 的 UI 元素引用（供页面清理用）
        public Border? DragGhost => _dragGhost;
        public IReadOnlyCollection<DragDropZoneInfo> ActiveZones => _overlayZones.Values;

        /// <summary>重新加载拖放配置。</summary>
        public async Task ReloadConfigAsync()
        {
            _dragZones = await _tracking.LoadDragZoneConfigAsync();
        }

        // ====================================================================
        // 旧内部拖拽路径 (Legacy) — 基于指针坐标 + GhostCard
        //
        // 当前 AnimeCard（CanDrag=True）已通过 HandlePointerPressed 中的
        // card.CanDrag 检查跳过此路径。此路径仍保留用于：
        //   - 未启用标准拖拽的页面兼容
        //   - GhostCard 视觉代码（后续 Drag Visual 阶段将重建）
        //
        // 数据事实来源：AnimeCardDragPayload（而非 GhostCard）
        // 主路径：标准拖拽（OnBodyDragStarting → HandleStandardDragOver/DropAsync）
        // ====================================================================

        /// <summary>
        /// [旧内部拖拽路径] 处理指针按下：从 OriginalSource 向上查找 AnimeCard，记录拖放起点。
        /// 如果 AnimeCard 已启用标准拖拽（CanDrag=true），跳过此路径。
        /// 注意：标准拖拽启用后，此方法仅用于旧页面兼容，不再作为 AnimeCard 主拖拽路径。
        /// </summary>
        public void HandlePointerPressed(UIElement pageRoot, PointerRoutedEventArgs e)
        {
            if (_isDragging) return;
            _dragPointerDown = true;
            _dragPointerDownPos = e.GetCurrentPoint(pageRoot).Position;

            _dragAnime = null;
            _dragPayload = null;
            // 从原始事件源向上遍历视觉树，查找 AnimeCard（O(depth)而非 O(cards×depth)）
            var element = e.OriginalSource as DependencyObject;
            while (element != null)
            {
                if (element is AnimeCard card)
                {
                    // 标准拖拽源启用时，旧内部拖拽路径不启动
                    if (card.CanDrag)
                    {
                        System.Diagnostics.Debug.WriteLine("[DragDropService] AnimeCard has CanDrag=true, skipping legacy drag path");
                        _dragPointerDown = false;
                        _dragAnime = null;
                        return;
                    }

                    _dragAnime = card.DataContext as Anime;
                    // 旧路径仍构造 payload 以保持数据一致性，但不作为标准拖拽的事实来源
                    if (_dragAnime != null)
                    {
                        _dragPayload = new AnimeCardDragPayload
                        {
                            AnimeId = _dragAnime.ID,
                            Title = _dragAnime.Title,
                            CoverImageUrl = _dragAnime.CoverURL,
                            Summary = _dragAnime.Description,
                            SeasonYear = _dragAnime.SeasonYear,
                            SeasonMonth = _dragAnime.SeasonMonth,
                            Source = "DragDropService",
                        };
                        System.Diagnostics.Debug.WriteLine($"[DragPayload] DragDropService received AnimeCardDragPayload: {_dragPayload.AnimeId} - {_dragPayload.Title}");
                    }
                    break;
                }
                element = VisualTreeHelper.GetParent(element);
            }
        }

        /// <summary>
        /// [旧内部拖拽路径] 处理指针移动：通过 Composition Offset 更新 Ghost 位置（合成线程，零布局开销）。
        /// 每次更新前检查左键状态和边界，防止 Ghost 在窗口外残影。
        /// 注意：此路径仅在旧内部拖拽活动时触发，标准拖拽（CanDrag=true）不经过此方法。
        /// GhostCard 后续将在 Drag Visual 阶段重建为自定义拖拽视觉。
        /// </summary>
        public bool HandlePointerMoved(UIElement pageRoot, UIElement overlay, PointerRoutedEventArgs e, params DragAction[] excludeActions)
        {
            if (_isDragging && _ghostVisual != null)
            {
                var pt = e.GetCurrentPoint(overlay).Position;

                // 检查左键是否仍按下（处理鼠标在窗口外释放的场景）
                var pointerProps = e.GetCurrentPoint(overlay).Properties;
                bool isLeftPressed = pointerProps.IsLeftButtonPressed;

                if (!isLeftPressed)
                {
                    CancelDrag(overlay, "left button released outside window");
                    return false;
                }

                // 检查指针是否超出拖拽宿主边界
                double tolerance = 8;
                bool outsideHost = false;
                if (overlay is FrameworkElement host)
                {
                    outsideHost = pt.X < -tolerance || pt.Y < -tolerance
                        || pt.X > host.ActualWidth + tolerance
                        || pt.Y > host.ActualHeight + tolerance;
                }

                if (outsideHost)
                {
                    CancelDrag(overlay, "pointer outside drag host");
                    return false;
                }

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
        /// [旧内部拖拽路径] 处理指针释放：触发放置或取消。
        /// </summary>
        public void HandlePointerReleased(UIElement overlay, PointerRoutedEventArgs e)
        {
            _dragPointerDown = false;
            if (!_isDragging) return;
            EndDrag(overlay, e.GetCurrentPoint(overlay).Position);
        }

        /// <summary>
        /// [旧内部拖拽路径] 处理指针取消。
        /// </summary>
        public void HandlePointerCanceled(UIElement overlay)
        {
            _dragPointerDown = false;
            if (!_isDragging) return;
            CancelDrag(overlay, "PointerCanceled event");
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

        private void CancelDrag(UIElement overlay) => CancelDrag(overlay, null);

        private void CancelDrag(UIElement overlay, string? reason)
        {
            if (!string.IsNullOrEmpty(reason))
                System.Diagnostics.Debug.WriteLine($"[DragDropService] CancelDrag called, reason = {reason}");
            CleanupDrag(overlay);
        }

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
                    System.Diagnostics.Debug.WriteLine("[DragDropService] ClearDragVisual called");
                    panel.Children.Remove(_dragGhost);
                }
                // 移除 zone
                foreach (var kv in _overlayZones)
                {
                    if (panel.Children.Contains(kv.Value.Border))
                        panel.Children.Remove(kv.Value.Border);
                }
            }

            ResetState();
        }

        /// <summary>
        /// 强制重置拖拽状态，不依赖 overlay 引用。用于指针捕获丢失、窗口失活等场景。
        /// </summary>
        public void ResetState()
        {
            if (_isDragging || _dragAnime != null || _dragPayload != null)
            {
                System.Diagnostics.Debug.WriteLine("[DragDropService] ResetState called - state cleared");
            }

            _isDragging = false;
            _dragAnime = null;
            _dragPayload = null;
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

                // 主标签：动作名称（追番、补番等）
                var label = new TextBlock
                {
                    Text = GetActionLabel(config.Action),
                    FontSize = 16,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                };

                // 提示文字：拖放时才显示（如"释放以标记为追番"）
                var hint = new TextBlock
                {
                    Text = GetActionHint(config.Action),
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromArgb(200, 220, 210, 240)),
                    Visibility = Visibility.Collapsed,
                    Margin = new Thickness(0, 4, 0, 0),
                };

                var innerStack = new StackPanel
                {
                    Spacing = 0,
                    Children = { label, hint },
                };

                var inner = new Border
                {
                    Child = innerStack,
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(18, 14, 18, 14),
                    Background = new SolidColorBrush(Color.FromArgb(160, 0x44, 0x88, 0xFF)),
                    Opacity = 0.75,
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

                // 注册标准 DropTarget 事件，支持 AnimeCardDragPayload 接收
#pragma warning disable CA1031 // zone 内异常不应影响拖放流程
                zone.DragOver += (s, args) =>
                {
                    if (args.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                    {
                        args.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                        // 高亮反馈 — DragDropService.HandleStandardDragOver 也会管理高亮
                        inner.Background = new SolidColorBrush(Color.FromArgb(240, 0x77, 0xBB, 0xFF));
                        inner.Opacity = 1.0;
                        hint.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        args.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
                    }
                };
                zone.DragLeave += (s, args) =>
                {
                    // 恢复默认外观
                    inner.Background = new SolidColorBrush(Color.FromArgb(160, 0x44, 0x88, 0xFF));
                    inner.Opacity = 0.75;
                    hint.Visibility = Visibility.Collapsed;
                };
                zone.Drop += async (s, args) =>
                {
                    System.Diagnostics.Debug.WriteLine("[InternalDropZone] Drop triggered");
                    if (!args.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                        return;

                    string? text;
                    try
                    {
                        text = await args.DataView.GetTextAsync();
                    }
                    catch
                    {
                        System.Diagnostics.Debug.WriteLine("[InternalDropZone] payload parse fail - GetTextAsync failed");
                        return;
                    }

                    if (string.IsNullOrEmpty(text))
                        return;

                    var payload = AnimeCardDragPayloadSerializer.Deserialize(text);
                    if (payload == null)
                    {
                        System.Diagnostics.Debug.WriteLine("[InternalDropZone] payload parse fail - invalid payload");
                        return;
                    }

                    // 通过共享路由方法处理，与 HandleStandardDropAsync 复用 Zone 命中逻辑
                    var dropPoint = args.GetPosition(overlay);
                    await RoutePayloadToZoneAsync(payload, dropPoint);

                    // 恢复默认外观
                    inner.Background = new SolidColorBrush(Color.FromArgb(160, 0x44, 0x88, 0xFF));
                    inner.Opacity = 0.75;
                    hint.Visibility = Visibility.Collapsed;
                };
#pragma warning restore CA1031

                if (pw > 0 && ph > 0)
                {
                    zone.Width = pw * config.WidthPercent;
                    zone.Height = ph * config.HeightPercent;
                    // Canvas 用附加属性定位，不触发 measure/arrange，避免拖放时布局干扰
                    Canvas.SetLeft(zone, pw * config.XPercent);
                    Canvas.SetTop(zone, ph * config.YPercent);
                }

                if (overlay is Canvas canvas) canvas.Children.Add(zone);
                _overlayZones[config.Id] = new DragDropZoneInfo(zone, inner, label, hint);
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
        /// 获取拖放提示文字（用于 DropZone 悬停时的第二行提示）。
        /// 纯视觉提示，不参与业务逻辑。
        /// </summary>
        public static string GetActionHint(DragAction action) => action switch
        {
            DragAction.Watching => "释放以标记为追番",
            DragAction.PlanToWatch => "释放以标记为补番",
            DragAction.NotInterested => "释放以设为不感兴趣",
            DragAction.Following => "释放以加入关注",
            DragAction.Completed => "释放以标记为已看完",
            DragAction.Dropped => "释放以标记为已弃番",
            DragAction.Blocked => "释放以加入屏蔽",
            _ => "释放至此区域"
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
        /// [旧内部拖拽路径] 查找拖放完成时的目标 Zone 并执行标记。
        /// 此方法仅由旧内部拖拽路径（HandlePointerReleased → EndDrag）调用。
        /// 标准拖拽路径使用 HandleStandardDropAsync / RoutePayloadToZoneAsync。
        /// </summary>
        public void ExecuteDrop(Point dropPoint)
        {
            if (_dragPayload != null)
                System.Diagnostics.Debug.WriteLine($"[DragPayload] DropZone received AnimeCardDragPayload: {_dragPayload.AnimeId} - {_dragPayload.Title}");

            System.Diagnostics.Debug.WriteLine("[DragDropService] Legacy DragDropService path still active = true");

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
                        {
                            System.Diagnostics.Debug.WriteLine($"[DragPayload] DropZone handled internal anime card drag: AnimeId={_dragAnime.ID}, Action={cfg.Action}");
                            _ = SetStatusSafelyAsync(_dragAnime.ID, st);
                        }
                    }
                    break;
                }
            }
        }

        /// <summary>
        /// 供 AnimeCardDropHost 调用的外部 Drop 路由。
        /// 根据坐标判断是否命中有效 Zone，命中则执行业务逻辑。
        /// 使用 RoutePayloadToZoneAsync 与标准拖拽共享路由逻辑。
        /// </summary>
        /// <param name="dropPoint">相对于 overlay 的坐标。</param>
        /// <param name="payload">反序列化的拖拽载荷。</param>
        /// <returns>是否命中有效 Zone 并执行业务。</returns>
        public async Task<bool> HandleExternalDrop(Point dropPoint, AnimeCardDragPayload payload)
        {
            System.Diagnostics.Debug.WriteLine($"[DragDropService] HandleExternalDrop called: pos=({dropPoint.X:F0},{dropPoint.Y:F0}), payload={payload.AnimeId}");

            var result = await RoutePayloadToZoneAsync(payload, dropPoint);
            if (!result)
                System.Diagnostics.Debug.WriteLine("[DragDropService] HandleExternalDrop no valid target, ignored");
            return result;
        }

        // ======== 标准拖拽（ActiveDropContext） ========

        /// <summary>
        /// 设置当前页面的标准拖放上下文。页面在 Loaded 时调用。
        /// </summary>
        public void SetActiveDropContext(UIElement pageRoot, Panel overlay, params DragAction[] excludeActions)
        {
            _standardPageRoot = pageRoot;
            _standardOverlay = overlay;
            _standardExcludeActions = excludeActions ?? Array.Empty<DragAction>();

            System.Diagnostics.Debug.WriteLine($"[DragDropService] ActiveDropContext set: page={pageRoot.GetType().Name}, overlay={overlay.Name}");
        }

        /// <summary>
        /// 清理当前页面的标准拖放上下文。页面在 Unloaded 时调用。
        /// </summary>
        public void ClearActiveDropContext(UIElement pageRoot)
        {
            if (!ReferenceEquals(_standardPageRoot, pageRoot))
                return;

            CancelStandardDrag();
            _standardPageRoot = null;
            _standardOverlay = null;
            _standardExcludeActions = Array.Empty<DragAction>();
            System.Diagnostics.Debug.WriteLine("[DragDropService] ActiveDropContext cleared");
        }

        /// <summary>
        /// 处理标准拖拽的 DragOver。由 AnimeCardDropHost 或页面调用。
        /// 根据坐标构建/显示 DropZone、管理高亮、更新 DragVisual 位置和 Zone 提示文字。
        /// </summary>
        /// <param name="e">DragEventArgs，用于 GetPosition 获取 overlay 坐标。</param>
        /// <param name="coordinateSource">用于 GetPosition 的参照元素，通常为 overlay 自身。</param>
        public void HandleStandardDragOver(DragEventArgs e, UIElement? _)
        {
            // 确保 DropZone 已构建
            if (_standardOverlay != null && _overlayZones.Count == 0)
            {
                _standardOverlay.Visibility = Visibility.Visible;
                _standardOverlay.UpdateLayout();
                BuildAndShowZones(_standardOverlay, _standardExcludeActions);
            }

            if (_standardOverlay == null || _overlayZones.Count == 0)
                return;

            var overlayPt = e.GetPosition(_standardOverlay);

            // 查找当前 Zone
            string? hitZoneId = null;
            foreach (var kv in _overlayZones)
            {
                var z = kv.Value.Border;
                var zx = Canvas.GetLeft(z);
                var zy = Canvas.GetTop(z);
                var zr = new Rect(zx, zy, z.ActualWidth, z.ActualHeight);
                if (zr.Contains(overlayPt))
                {
                    hitZoneId = kv.Key;
                    break;
                }
            }

            // 高亮 + 提示文字管理（仅在切换时更新，不触发日志风暴）
            if (hitZoneId != _standardCurrentZoneId)
            {
                // 清除旧高亮
                if (_standardCurrentZoneId != null && _overlayZones.TryGetValue(_standardCurrentZoneId, out var oldZone))
                {
                    oldZone.Inner.Background = new SolidColorBrush(Color.FromArgb(160, 0x44, 0x88, 0xFF));
                    oldZone.Inner.Opacity = 0.75;
                    oldZone.HintText.Visibility = Visibility.Collapsed;
                }

                // 设置新高亮
                if (hitZoneId != null && _overlayZones.TryGetValue(hitZoneId, out var newZone))
                {
                    // 使用更亮的强调色表示活跃目标
                    newZone.Inner.Background = new SolidColorBrush(Color.FromArgb(240, 0x77, 0xBB, 0xFF));
                    newZone.Inner.Opacity = 1.0;
                    newZone.HintText.Visibility = Visibility.Visible;
                }

                _standardCurrentZoneId = hitZoneId;
            }
        }

        /// <summary>
        /// 处理标准拖拽的 Drop。读取 payload 并路由到有效 Zone。
        /// </summary>
        public async Task<bool> HandleStandardDropAsync(DragEventArgs e, UIElement? _)
        {
            System.Diagnostics.Debug.WriteLine("[DragDropService] StandardDrop called");

            try
            {
                if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                {
                    System.Diagnostics.Debug.WriteLine("[DragDropService] StandardDrop no text data");
                    return false;
                }

                var text = await e.DataView.GetTextAsync();
                if (string.IsNullOrEmpty(text))
                {
                    System.Diagnostics.Debug.WriteLine("[DragDropService] StandardDrop empty text");
                    return false;
                }

                var payload = AnimeCardDragPayloadSerializer.Deserialize(text);
                if (payload == null)
                {
                    System.Diagnostics.Debug.WriteLine("[DragDropService] StandardDrop payload parse fail");
                    return false;
                }

                System.Diagnostics.Debug.WriteLine($"[DragDropService] StandardDrop payload parse success: animeId={payload.AnimeId}");

                if (_standardOverlay == null || _overlayZones.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[DragDropService] StandardDrop no active drop context");
                    return false;
                }

                var overlayPt = e.GetPosition(_standardOverlay);

                var routed = await RoutePayloadToZoneAsync(payload, overlayPt);
                if (!routed)
                    System.Diagnostics.Debug.WriteLine("[DragDropService] StandardDrop no valid zone, ignored");
                return routed;
            }
            finally
            {
                CancelStandardDrag();
                System.Diagnostics.Debug.WriteLine("[DragDropService] StandardDrag cleanup");
            }
        }

        /// <summary>
        /// 将 AnimeCardDragPayload 路由到坐标命中的 Zone，并执行标记状态写入。
        /// 此方法供 HandleStandardDropAsync 和 Zone Drop 事件共享，减少路由逻辑重复。
        /// </summary>
        /// <param name="payload">已反序列化的拖拽载荷。</param>
        /// <param name="overlayPoint">相对于 overlay 的 Drop 坐标。</param>
        /// <returns>是否命中有效 Zone 并执行标记。</returns>
        private async Task<bool> RoutePayloadToZoneAsync(AnimeCardDragPayload payload, Point overlayPoint)
        {
            foreach (var kv in _overlayZones)
            {
                var z = kv.Value.Border;
                var zx = Canvas.GetLeft(z);
                var zy = Canvas.GetTop(z);
                var zr = new Rect(zx, zy, z.ActualWidth, z.ActualHeight);
                if (zr.Contains(overlayPoint))
                {
                    var cfg = _dragZones.Find(c => c.Id == kv.Key);
                    if (cfg != null && cfg.Action != DragAction.None)
                    {
                        var st = DragActionToStatus(cfg.Action);
                        if (st != AnimeTrackingStatus.None)
                        {
                            System.Diagnostics.Debug.WriteLine($"[DragPayload] RoutePayloadToZone: zone={cfg.Id}, action={cfg.Action}, animeId={payload.AnimeId}");
                            await _tracking.SetStatusAsync(payload.AnimeId, st);
                            return true;
                        }
                    }
                    break;
                }
            }
            return false;
        }

        /// <summary>
        /// 取消标准拖拽并清理 DropZone 覆盖层、高亮、提示文字和拖拽状态。
        /// </summary>
        public void CancelStandardDrag()
        {
            HideAllZoneHints();

            if (_standardOverlay != null)
            {
                ClearZonesFrom(_standardOverlay);
                _standardOverlay.Visibility = Visibility.Collapsed;
            }

            _standardCurrentZoneId = null;
            System.Diagnostics.Debug.WriteLine("[DragDropService] CancelStandardDrag - zones hidden, state cleared");
        }

        /// <summary>
        /// 隐藏所有 Zone 的提示文字。在取消拖拽或切换 Zone 时调用。
        /// </summary>
        private void HideAllZoneHints()
        {
            foreach (var kv in _overlayZones)
            {
                kv.Value.HintText.Visibility = Visibility.Collapsed;
            }
        }

        // ======== 页面级标准拖拽宿主注册 ========

        private readonly List<UIElement> _pageDragHosts = new();

        /// <summary>
        /// 在 BasePlugin 页面根元素上注册标准拖拽宿主。
        /// 使用 AddHandler(handledEventsToo=true) 确保不被子控件拦截。
        /// 页面在 Loaded 时调用。
        /// </summary>
        public void RegisterStandardDragHost(UIElement host)
        {
            if (host == null || _pageDragHosts.Contains(host))
                return;

            host.AllowDrop = true;

            host.AddHandler(UIElement.DragOverEvent,
                new DragEventHandler(OnPageDragOver), true);
            host.AddHandler(UIElement.DropEvent,
                new DragEventHandler(OnPageDrop), true);

            _pageDragHosts.Add(host);
            System.Diagnostics.Debug.WriteLine($"[DragDropService] Page standard drag host registered = {host.GetType().Name}");
        }

        /// <summary>
        /// 注销页面级标准拖拽宿主。页面在 Unloaded 时调用。
        /// </summary>
        public void UnregisterStandardDragHost(UIElement host)
        {
            if (host == null || !_pageDragHosts.Contains(host))
                return;

            host.RemoveHandler(UIElement.DragOverEvent,
                new DragEventHandler(OnPageDragOver));
            host.RemoveHandler(UIElement.DropEvent,
                new DragEventHandler(OnPageDrop));

            _pageDragHosts.Remove(host);
            System.Diagnostics.Debug.WriteLine($"[DragDropService] Page standard drag host unregistered = {host.GetType().Name}");
        }

        private void OnPageDragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                // 只更新高亮，不修改 AcceptedOperation
                HandleStandardDragOver(e, _standardOverlay ?? sender as UIElement);

                // 强制设置 AcceptedOperation = Copy，确保不被任何路径修改
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                e.Handled = true;
                e.DragUIOverride.IsCaptionVisible = false;
                e.DragUIOverride.IsGlyphVisible = false;
                e.DragUIOverride.IsContentVisible = false;
            }
        }

        private async void OnPageDrop(object sender, DragEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"[DragDropService] Page standard drag host Drop triggered, sender = {sender?.GetType().Name}");

            await HandleStandardDropAsync(e, _standardOverlay ?? sender as UIElement);
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
    /// HintText 用于拖放悬停时的第二行提示文字（如"释放以标记为追番"）。
    /// </summary>
    public record DragDropZoneInfo(
        Border Border,
        Border Inner,
        TextBlock Label,
        TextBlock HintText);
}
