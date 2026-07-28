using AniMeido.Contracts.DragDrop;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using System.Numerics;

namespace AniMeido.Plugin.Base.Services
{
    /// <summary>
    /// 拖放标记服务：管理 Zone 构建、标记状态路由与标准拖放协调。
    ///
    /// == 拖拽系统说明 ==
    ///
    /// [数据事实]
    ///   AnimeCardDragPayload — 跨窗口/跨区域的统一拖拽数据格式。
    ///   所有拖拽事件的数据来源均为 AnimeCardDragPayload JSON（StandardDataFormats.Text）。
    ///
    /// [Drag Source]
    ///   AnimeCard 本体标准拖拽（CanDrag=True, OnBodyDragStarting）— 唯一入口。
    ///
    /// [主窗口拖拽接收]
    ///   AnimeCardDropHost (Shell 级 fallback) → DragDropService.HandleStandardDragOver/DropAsync
    ///   → BuildAndShowZones → zone DragOver/Drop 事件 → TrackingService.SetStatusAsync
    ///
    /// [旧内部拖拽路径]
    ///   基于 Pointer 坐标与 GhostCard 的旧内部拖拽路径已删除。
    ///   所有 AnimeCard 使用标准拖拽：CanDrag + DragStarting + RegisterStandardDragHost。
    /// </summary>
    public sealed class DragDropService
    {
        // 高频拖拽日志开关——Debug 模式下关闭以避免卡顿
        private const bool VerboseDragLog = false;

        private readonly TrackingService _tracking;

        // Zone 状态
        private List<DragZoneConfig> _dragZones = DragZoneConfig.GetDefaults();
        private readonly Dictionary<string, ZoneVisual> _overlayZones = new();

        // 标准拖拽（ActiveDropContext）状态
        private UIElement? _standardPageRoot;
        private Panel? _standardOverlay;
        private DragAction[] _standardExcludeActions = Array.Empty<DragAction>();
        private string? _standardCurrentZoneId;



        public DragDropService(TrackingService tracking)
        {
            _tracking = tracking;
        }

        /// <summary>重新加载拖放配置。</summary>
        public async Task ReloadConfigAsync()
        {
            _dragZones = await _tracking.LoadDragZoneConfigAsync();
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
                if (overlay is Panel p) p.Children.Remove(kv.Value.Root);
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
                    }
                    else
                    {
                        args.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
                    }
                };
                zone.DragLeave += (s, args) =>
                {
                    // hover 视觉由 HandleStandardDragOver 统一管理
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

                    // 视觉由 CancelStandardDrag 统一清理
                };
#pragma warning restore CA1031

                if (pw > 0 && ph > 0)
                {
                    zone.Width = pw * config.WidthPercent;
                    zone.Height = ph * config.HeightPercent;
                    Canvas.SetLeft(zone, pw * config.XPercent);
                    Canvas.SetTop(zone, ph * config.YPercent);

                    if (overlay is Canvas canvas) canvas.Children.Add(zone);
                    _overlayZones[config.Id] = new ZoneVisual(zone, inner, hint);
                }
            }
        }

        /// <summary>
        /// 当移除 zone 时调用（服务不再管理 overlay 的孩子，由页面清理）。
        /// </summary>
        public void ClearZonesFrom(UIElement overlay)
        {
            foreach (var kv in _overlayZones)
            {
                if (overlay is Panel p) p.Children.Remove(kv.Value.Root);
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
                var z = kv.Value.Root;
                var zx = Canvas.GetLeft(z);
                var zy = Canvas.GetTop(z);
                var zr = new Rect(zx, zy, z.ActualWidth, z.ActualHeight);
                if (zr.Contains(overlayPt))
                {
                    hitZoneId = kv.Key;
                    break;
                }
            }

            // 始终确保标准圆形 DragUI 显示（不隐藏）
            e.DragUIOverride.IsContentVisible = true;
            e.DragUIOverride.IsCaptionVisible = false;
            e.DragUIOverride.IsGlyphVisible = false;

            if (hitZoneId != null)
            {
                // 高亮 + 展开尾条（仅在 zone 切换时）
                if (hitZoneId != _standardCurrentZoneId)
                {
                    if (_standardCurrentZoneId != null && _overlayZones.TryGetValue(_standardCurrentZoneId, out var oldZone))
                        UnhighlightZone(oldZone);

                    if (_overlayZones.TryGetValue(hitZoneId, out var newZone))
                    {
                        HighlightZone(newZone);
                    }

                    ShowTailPreview(hitZoneId, overlayPt);
                    _standardCurrentZoneId = hitZoneId;
                }
                else
                {
                    // 每次 DragOver 都更新位置（RenderTransform，不触发布局）
                    UpdateTailPreviewPosition(overlayPt);
                }
            }
            else
            {
                if (_standardCurrentZoneId != null
                    && _overlayZones.TryGetValue(_standardCurrentZoneId, out var oldZone))
                {
                    UnhighlightZone(oldZone);
                }

                HideTailPreview();
                _standardCurrentZoneId = null;
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
                var z = kv.Value.Root;
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

        // ======== DropZone 高亮 + 展开态 DragToken ========

        /// <summary>高亮 DropZone（不改变尺寸）。</summary>
        private static void HighlightZone(ZoneVisual zone)
        {
            zone.Inner.Background = new SolidColorBrush(Color.FromArgb(240, 0x77, 0xBB, 0xFF));
            zone.Inner.Opacity = 1.0;
            zone.HintText.Visibility = Visibility.Visible;
        }

        /// <summary>恢复 DropZone 普通外观。</summary>
        private static void UnhighlightZone(ZoneVisual zone)
        {
            zone.Inner.Background = new SolidColorBrush(Color.FromArgb(160, 0x44, 0x88, 0xFF));
            zone.Inner.Opacity = 0.75;
            zone.HintText.Visibility = Visibility.Collapsed;
        }

        // 展开尾条（进入 DropZone 时从标准圆形 DragToken 后方伸出）
        private FrameworkElement? _dragTokenTailPreview;
        private string? _dragTokenTailZoneId;
        private bool _dragTokenTailExpanded;
        private Border? _dragTokenTailPill;
        private TextBlock? _dragTokenTailHintLabel;

        // Composition visuals（替代 XAML CompositeTransform + Storyboard）
        private Visual? _dragTokenTailRootVisual;   // Offset 跟随光标
        private Visual? _dragTokenTailPillVisual;   // Scale.X + Opacity 展开
        private Visual? _dragTokenTailHintVisual;   // Opacity 展开

        // 固定尺寸
        private const double TailExpandedWidth = 230;
        private const double TailHeight = 52;
        private const double DragTokenRadius = 36;
        private const double TailStartOverlap = 4;
        private const double TailVerticalOffset = 26;

        /// <summary>创建尾条 UI（仅首次调用时执行）。</summary>
        private void EnsureTailPreviewCreated(Canvas canvas, string hintText)
        {
            if (_dragTokenTailPreview != null)
                return;

            _dragTokenTailHintLabel = new TextBlock
            {
                Text = hintText,
                FontSize = 15,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(76, 0, 18, 0),
                IsHitTestVisible = false,
                Opacity = 0,
            };

            _dragTokenTailPill = new Border
            {
                Child = _dragTokenTailHintLabel,
                Width = TailExpandedWidth,
                Height = TailHeight,
                CornerRadius = new CornerRadius(TailHeight / 2),
                Background = new SolidColorBrush(Color.FromArgb(210, 0x22, 0x66, 0xDD)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Opacity = 0,
            };

            var root = new Grid
            {
                Width = TailExpandedWidth,
                Height = TailHeight,
                IsHitTestVisible = false,
                Children = { _dragTokenTailPill },
            };

            Canvas.SetLeft(root, 0);
            Canvas.SetTop(root, 0);
            Canvas.SetZIndex(root, 9999);
            canvas.Children.Add(root);
            _dragTokenTailPreview = root;

            // 获取 Composition visuals
            _dragTokenTailRootVisual = ElementCompositionPreview.GetElementVisual(root);
            _dragTokenTailRootVisual.Offset = Vector3.Zero;

            _dragTokenTailPillVisual = ElementCompositionPreview.GetElementVisual(_dragTokenTailPill);
            _dragTokenTailPillVisual.CenterPoint = new Vector3(0f, (float)TailHeight / 2f, 0f);
            _dragTokenTailPillVisual.Scale = new Vector3(0f, 1f, 1f);
            _dragTokenTailPillVisual.Opacity = 0f;

            _dragTokenTailHintVisual = ElementCompositionPreview.GetElementVisual(_dragTokenTailHintLabel);
            _dragTokenTailHintVisual.Opacity = 0f;

            _dragTokenTailExpanded = false;
        }

        /// <summary>使用 Composition Visual.Offset 立即更新尾条位置。</summary>
        private void UpdateTailPreviewPosition(Point overlayPointerPos)
        {
            if (_dragTokenTailRootVisual == null)
                return;

            _dragTokenTailRootVisual.Offset = new Vector3(
                (float)(overlayPointerPos.X - DragTokenRadius + TailStartOverlap),
                (float)(overlayPointerPos.Y - TailVerticalOffset),
                0f);
        }

        /// <summary>播放 Composition 展开动画（替代 XAML Storyboard）。</summary>
        private void PlayTailExpandCompositionAnimation()
        {
            if (_dragTokenTailPillVisual == null || _dragTokenTailHintVisual == null)
                return;

            var compositor = _dragTokenTailPillVisual.Compositor;

            // 重置到初始状态
            _dragTokenTailPillVisual.Scale = new Vector3(0f, 1f, 1f);
            _dragTokenTailPillVisual.Opacity = 0f;
            _dragTokenTailHintVisual.Opacity = 0f;

            var easing = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.2f, 0.0f),
                new Vector2(0.0f, 1.0f));

            // Scale.X: 0 → 1 (110ms)
            var scaleAnim = compositor.CreateScalarKeyFrameAnimation();
            scaleAnim.InsertKeyFrame(0f, 0f);
            scaleAnim.InsertKeyFrame(1f, 1f, easing);
            scaleAnim.Duration = TimeSpan.FromMilliseconds(110);

            // pill Opacity: 0 → 1 (70ms)
            var pillOpacity = compositor.CreateScalarKeyFrameAnimation();
            pillOpacity.InsertKeyFrame(0f, 0f);
            pillOpacity.InsertKeyFrame(1f, 1f);
            pillOpacity.Duration = TimeSpan.FromMilliseconds(70);

            // hint Opacity: 0 → 1 (110ms)
            var hintOpacity = compositor.CreateScalarKeyFrameAnimation();
            hintOpacity.InsertKeyFrame(0f, 0f);
            hintOpacity.InsertKeyFrame(1f, 1f);
            hintOpacity.Duration = TimeSpan.FromMilliseconds(110);

            _dragTokenTailPillVisual.StartAnimation("Scale.X", scaleAnim);
            _dragTokenTailPillVisual.StartAnimation("Opacity", pillOpacity);
            _dragTokenTailHintVisual.StartAnimation("Opacity", hintOpacity);
        }

        /// <summary>显示并展开尾条（或 zone 切换时重置并重播动画）。</summary>
        private void ShowTailPreview(string zoneId, Point overlayPointerPos)
        {
            if (_standardOverlay is not Canvas canvas)
                return;

            var cfg = _dragZones.Find(c => c.Id == zoneId);
            var hintText = cfg != null ? GetActionHint(cfg.Action) : "释放以标记";

            // 创建 UI（首次）
            EnsureTailPreviewCreated(canvas, hintText);

            // === zone 切换时更新文案 + 重置 Composition 状态 ===
            if (_dragTokenTailZoneId != zoneId)
            {
                if (_dragTokenTailHintLabel != null)
                    _dragTokenTailHintLabel.Text = hintText;

                if (_dragTokenTailPillVisual != null)
                {
                    _dragTokenTailPillVisual.StopAnimation("Scale.X");
                    _dragTokenTailPillVisual.StopAnimation("Opacity");
                    _dragTokenTailPillVisual.Scale = new Vector3(0f, 1f, 1f);
                    _dragTokenTailPillVisual.Opacity = 0f;
                }

                if (_dragTokenTailHintVisual != null)
                {
                    _dragTokenTailHintVisual.StopAnimation("Opacity");
                    _dragTokenTailHintVisual.Opacity = 0f;
                }

                _dragTokenTailExpanded = false;
            }

            if (_dragTokenTailPreview != null)
                _dragTokenTailPreview.Visibility = Visibility.Visible;

            // === 立即更新位置 ===
            UpdateTailPreviewPosition(overlayPointerPos);

            _dragTokenTailZoneId = zoneId;

            // === 播放 Composition 展开动画 ===
            if (!_dragTokenTailExpanded)
            {
                _dragTokenTailExpanded = true;
                PlayTailExpandCompositionAnimation();
            }
        }

        /// <summary>隐藏横条尾条。</summary>
        private void HideTailPreview()
        {
            if (_dragTokenTailPreview != null)
                _dragTokenTailPreview.Visibility = Visibility.Collapsed;

            if (_dragTokenTailPillVisual != null)
            {
                _dragTokenTailPillVisual.StopAnimation("Scale.X");
                _dragTokenTailPillVisual.StopAnimation("Opacity");
                _dragTokenTailPillVisual.Scale = new Vector3(0f, 1f, 1f);
                _dragTokenTailPillVisual.Opacity = 0f;
            }

            if (_dragTokenTailHintVisual != null)
            {
                _dragTokenTailHintVisual.StopAnimation("Opacity");
                _dragTokenTailHintVisual.Opacity = 0f;
            }

            if (_dragTokenTailPill != null)
                _dragTokenTailPill.Opacity = 0;
            if (_dragTokenTailHintLabel != null)
                _dragTokenTailHintLabel.Opacity = 0;

            _dragTokenTailZoneId = null;
            _dragTokenTailExpanded = false;
        }

        /// <summary>移除横条尾条并从 overlay 删除。</summary>
        private void RemoveTailPreview()
        {
            if (_dragTokenTailPreview != null && _standardOverlay is Panel panel)
                panel.Children.Remove(_dragTokenTailPreview);

            _dragTokenTailPreview = null;
            _dragTokenTailPill = null;
            _dragTokenTailHintLabel = null;
            _dragTokenTailRootVisual = null;
            _dragTokenTailPillVisual = null;
            _dragTokenTailHintVisual = null;
            _dragTokenTailZoneId = null;
            _dragTokenTailExpanded = false;
        }

        /// <summary>
        /// 取消标准拖拽并清理 DropZone 覆盖层、高亮、提示文字和拖拽状态。
        /// </summary>
        public void CancelStandardDrag()
        {
            HideAllZoneHints();
            RemoveTailPreview();

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
        }

        private void OnPageDragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                HandleStandardDragOver(e, _standardOverlay ?? sender as UIElement);

                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                e.Handled = true;
                e.DragUIOverride.IsCaptionVisible = false;
                e.DragUIOverride.IsGlyphVisible = false;
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
    /// Zone 的 UI 元素记录。
    /// </summary>
    internal sealed record ZoneVisual(
        Border Root,
        Border Inner,
        TextBlock HintText);
}
