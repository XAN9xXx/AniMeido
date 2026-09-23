using System.Runtime.InteropServices;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class TodayPage
{
    private Image? _planHandoffImage;
    private bool _capturingPlan;
    private int _planOpenGeneration;
    private int _planHandoffFrames;
    private Storyboard? _planStackAnimation;
    private TodayPlanStackPanel? _planPanel;
    private bool _closingPlanStack;
    private bool _movingPlanSurface;
    private double _collapsedStackHeight;
    private double _expandedStackHeight;
    private AppWindow? _planOwnerWindow;
    private InputActivationListener? _planActivationListener;

    private void OnPlanPanelLoaded(object sender, RoutedEventArgs e)
        => _planPanel = (TodayPlanStackPanel)sender;

    private async void OnPlanDeckTapped(object sender, TappedRoutedEventArgs e)
    {
        if (!PlanStackPopup.IsOpen && ViewModel.HasOverflowPlans && ReferenceEquals(e.OriginalSource, sender))
        {
            e.Handled = true;
            await TryOpenPlanStackAsync();
        }
    }

    private async void OnPlanDeckKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!PlanStackPopup.IsOpen && ViewModel.HasOverflowPlans
            && ReferenceEquals(e.OriginalSource, sender)
            && e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
        {
            e.Handled = true;
            await TryOpenPlanStackAsync();
        }
    }

    /// <summary>展开卡包；失败时把卡包归位并提示，不让异常冒到应用层。</summary>
    private async Task TryOpenPlanStackAsync()
    {
        try
        {
            await OpenPlanStackAsync();
        }
#pragma warning disable CA1031 // 卡包展开只是呈现方式，任何失败都归位并提示，不打断今天页。
        catch (Exception ex)
        {
            _movingPlanSurface = false;
            if (PlanStackPopup.IsOpen)
            {
                // 关闭回调会归位卡包并退订窗口事件。
                PlanStackPopup.IsOpen = false;
            }
            else
            {
                RestorePlanSurface();
                PlanCard.Height = double.NaN;
                DetachPlanWindowEvents();
            }

            if (IsLoaded)
                ShowNotification($"补番计划没能展开：{ex.Message}", InfoBarSeverity.Warning);
        }
#pragma warning restore CA1031
    }

    private async Task OpenPlanStackAsync()
    {
        if (!IsLoaded || _capturingPlan || PlanStackPopup.IsOpen || _planPanel is null || PlanSurface.ActualWidth <= 0)
            return;

        // Popup 内容不能直接用 RenderTargetBitmap 捕获；在迁移前保存原位折叠画面。
        var generation = ++_planOpenGeneration;
        var originalSize = new Size(PlanVisual.ActualWidth, PlanVisual.ActualHeight);
        var originalPosition = PlanVisual.TransformToVisual(null).TransformPoint(new Point());
        var bitmap = new RenderTargetBitmap();
        _capturingPlan = true;
        try
        {
            await bitmap.RenderAsync(PlanVisual);
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or ArgumentException)
        {
            // 截图只是视觉增强，失败不阻止卡包操作。
            bitmap = null;
        }
        finally
        {
            _capturingPlan = false;
        }
        if (!IsLoaded || generation != _planOpenGeneration || PlanStackPopup.IsOpen
            || !ViewModel.HasOverflowPlans
            || originalSize != new Size(PlanVisual.ActualWidth, PlanVisual.ActualHeight)
            || originalPosition != PlanVisual.TransformToVisual(null).TransformPoint(new Point())) return;
        if (bitmap is { PixelWidth: > 0, PixelHeight: > 0 })
        {
            _planHandoffImage = new Image
            {
                Source = bitmap, Width = originalSize.Width, Height = originalSize.Height,
                Stretch = Stretch.Fill, IsHitTestVisible = false,
            };
        }

        var origin = PlanSurface.TransformToVisual(null).TransformPoint(new Point());
        _collapsedStackHeight = PlanSurface.ActualHeight;
        var width = PlanSurface.ActualWidth;
        var availableHeight = AvailablePlanStackHeight(origin);
        var chromeHeight = _collapsedStackHeight - PlanScroll.ActualHeight;
        // 最多容纳五张完整卡片；更多作品保留在同一列表中，由浮层内部滚动。
        var visibleRowsHeight = _planPanel.Children.Take(5).Sum(row => row.DesiredSize.Height);
        _expandedStackHeight = Math.Max(1, Math.Min(availableHeight,
            Math.Max(_collapsedStackHeight, visibleRowsHeight + chromeHeight)));

        PlanStackPopup.HorizontalOffset = 0;
        PlanStackPopup.VerticalOffset = 0;
        var popupOrigin = PlanStackPopup.TransformToVisual(null).TransformPoint(new Point());
        // 点击层覆盖整个客户区（含侧栏），真实卡包仍在原坐标；不复制底下页面。
        PlanStackPopup.HorizontalOffset = -popupOrigin.X;
        PlanStackPopup.VerticalOffset = -popupOrigin.Y;
        PlanDismissCanvas.Width = Math.Max(XamlRoot.Size.Width, origin.X + width);
        PlanDismissCanvas.Height = Math.Max(XamlRoot.Size.Height, origin.Y + _expandedStackHeight);
        Canvas.SetLeft(PlanVisual, origin.X);
        Canvas.SetTop(PlanVisual, origin.Y);

        // 仅留下透明尺寸占位，底板、标题、卡片和通知栏一起迁移，原位没有副本。
        _movingPlanSurface = true;
        PlanCard.Height = _collapsedStackHeight;
        PlanVisual.Width = width;
        PlanVisual.Height = _expandedStackHeight;
        PlanCard.Children.Remove(PlanVisual);
        PlanDismissCanvas.Children.Add(PlanVisual);
        PlanScroll.VerticalScrollMode = ScrollMode.Enabled;
        // 仅在浮层尚未显示时准备展开布局。显示后再 Arrange 到 (0, 0)
        // 会覆盖 Popup 分配的位置，使整个卡包在收起动画期间短暂错位。
        _planPanel.Expanded = true;
        var popupSize = new Size(PlanDismissCanvas.Width, PlanDismissCanvas.Height);
        PlanDismissCanvas.Measure(popupSize);
        PlanDismissCanvas.Arrange(new Rect(new Point(), popupSize));
        PlanDismissCanvas.UpdateLayout();
        // 原位底板与浮层底板是同一个不透明控件；先准备首帧，再显示原生浮层。
        AnimatePlanStack(closing: false, startImmediately: false);
        PlanStackPopup.IsOpen = true;
        _movingPlanSurface = false;
    }

    private double AvailablePlanStackHeight(Point origin)
    {
        var windowId = XamlRoot.ContentIslandEnvironment.AppWindowId;
        // 只订阅一次：浮层关闭时退订，展开中途失败时由 TryOpenPlanStackAsync 退订。
        if (_planOwnerWindow is null)
        {
            _planOwnerWindow = AppWindow.GetFromWindowId(windowId);
            _planOwnerWindow.Changed += OnPlanOwnerChanged;
            _planActivationListener = InputActivationListener.GetForWindowId(windowId);
            _planActivationListener.InputActivationChanged += OnPlanActivationChanged;
        }
        var handle = Microsoft.UI.Win32Interop.GetWindowFromWindowId(windowId);
        var scale = XamlRoot.RasterizationScale;
        var point = new Windows.Graphics.PointInt32(
            (int)Math.Round(origin.X * scale), (int)Math.Round(origin.Y * scale));
        if (!PlanClientToScreen(handle, ref point))
            return Math.Max(1, XamlRoot.Size.Height - origin.Y - 12);
        var area = DisplayArea.GetFromPoint(point, DisplayAreaFallback.Nearest).WorkArea;
        return Math.Max(1, (area.Y + area.Height - point.Y) / scale - 12);
    }

    private void OnPlanOwnerChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange)
            PlanStackPopup.IsOpen = false;
    }

    private void OnPlanOutsidePressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsWithinPlanSurface(e.OriginalSource as DependencyObject)) return;
        e.Handled = true;
        ClosePlanStack();
    }

    private void OnPlanOutsideWheel(object sender, PointerRoutedEventArgs e)
    {
        if (IsWithinPlanSurface(e.OriginalSource as DependencyObject)) return;
        e.Handled = true;
        ClosePlanStack();
    }

    private bool IsWithinPlanSurface(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, PlanVisual)) return true;
        return false;
    }

    private void OnPlanActivationChanged(InputActivationListener sender, InputActivationListenerActivationChangedEventArgs args)
    {
        // 点击别的应用或 Alt+Tab 也先收起；不依赖全局鼠标钩子。
        if (sender.State == InputActivationState.Deactivated)
            ClosePlanStack();
    }

    [DllImport("user32.dll", EntryPoint = "ClientToScreen")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlanClientToScreen(nint window, ref Windows.Graphics.PointInt32 point);

    private void OnPlanStackOpened(object sender, object e)
    {
        _planPanel?.Focus(FocusState.Programmatic);
        _planStackAnimation?.Begin();
    }

    private void AnimatePlanStack(bool closing, bool startImmediately = true)
    {
        if (_planPanel is null) return;
        _planStackAnimation?.Stop();
        // 已显示浮层的位置由 Popup 管理；收起只做渲染变换，不能重新 Arrange 根容器。
        if (!new UISettings().AnimationsEnabled)
        {
            if (closing)
            {
                RestorePlanSurface();
                PlanStackPopup.IsOpen = false;
            }
            return;
        }
        var animation = new Storyboard();
        var ratio = Math.Clamp(_collapsedStackHeight / _expandedStackHeight, 0.01, 1);
        var scale = new ScaleTransform { ScaleY = closing ? 1 : ratio };
        PlanBackdrop.RenderTransform = scale;
        var clipScale = new ScaleTransform { ScaleY = closing ? 1 : ratio };
        PlanSurface.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, PlanVisual.Width, _expandedStackHeight), Transform = clipScale,
        };
        var footer = new TranslateTransform { Y = closing ? 0 : _collapsedStackHeight - _expandedStackHeight };
        PlanFooter.RenderTransform = footer;
        AddStackMotion(animation, scale, nameof(ScaleTransform.ScaleY), closing ? 1 : ratio, closing ? ratio : 1);
        AddStackMotion(animation, clipScale, nameof(ScaleTransform.ScaleY), closing ? 1 : ratio, closing ? ratio : 1);
        AddStackMotion(animation, footer, nameof(TranslateTransform.Y),
            closing ? 0 : _collapsedStackHeight - _expandedStackHeight,
            closing ? _collapsedStackHeight - _expandedStackHeight : 0);
        if (closing && PlanScroll.VerticalOffset > 0)
        {
            // 列表滚动过时，用渲染位移衔接回顶部，避免 ChangeView 在收起前跳一下。
            var scrollReturn = new TranslateTransform();
            VisiblePlanList.RenderTransform = scrollReturn;
            AddStackMotion(animation, scrollReturn, nameof(TranslateTransform.Y), 0, PlanScroll.VerticalOffset);
        }
        var thirdTop = _planPanel.Children.Take(2).Sum(child => child.DesiredSize.Height);
        var y = thirdTop;
        for (var index = 2; index < _planPanel.Children.Count; index++)
        {
            var row = _planPanel.Children[index];
            if (index >= 3)
            {
                var offset = thirdTop - y;
                var angle = Math.Min(index - 2, 3) * 3d;
                var transform = new CompositeTransform
                {
                    TranslateY = closing ? 0 : offset, Rotation = closing ? 0 : angle,
                };
                row.RenderTransform = transform;
                AddStackMotion(animation, transform, nameof(CompositeTransform.TranslateY), closing ? 0 : offset, closing ? offset : 0);
                AddStackMotion(animation, transform, nameof(CompositeTransform.Rotation), closing ? 0 : angle, closing ? angle : 0);
                if (index >= 6)
                    AddStackMotion(animation, row, nameof(Opacity), closing ? 1 : 0, closing ? 0 : 1);
            }
            y += row.DesiredSize.Height;
        }
        PlanScroll.IsHitTestVisible = false;
        _planPanel.IsAnimating = true;
        _planStackAnimation = animation;
        animation.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_planStackAnimation, animation)) return;
            // 收起时保持最后一帧直到浮层关闭，不能先恢复展开图再隐藏。
            if (closing)
            {
                CompletePlanStackClose();
                return;
            }
            animation.Stop();
            _planStackAnimation = null;
            ResetPlanMotion();
            PlanScroll.IsHitTestVisible = true;
            if (_closingPlanStack) AnimatePlanStack(closing: true);
            else if (_pendingPlan is { } entry) ScrollToPlan(entry);
        };
        if (startImmediately) animation.Begin();
    }

    private void ResetPlanMotion()
    {
        PlanSurface.Clip = null;
        PlanBackdrop.RenderTransform = null;
        PlanFooter.RenderTransform = null;
        VisiblePlanList.RenderTransform = null;
        if (_planPanel is null) return;
        _planPanel.IsAnimating = false;
        foreach (var row in _planPanel.Children)
        {
            row.RenderTransform = new CompositeTransform();
            row.Opacity = 1;
        }
        _planPanel.InvalidateArrange();
    }

    private static void AddStackMotion(Storyboard storyboard, DependencyObject target,
        string property, double from, double to)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(360)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = false,
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    private TodayPlanEntry? _pendingPlan;

    private void ScrollToPlan(TodayPlanEntry entry)
    {
        if (!PlanStackPopup.IsOpen)
            return;
        if (_planStackAnimation is not null)
        {
            _pendingPlan = entry;
            return;
        }
        _pendingPlan = null;
        // 条目可能已被原地替换（例如补上封面），按作品查找位置。
        var index = -1;
        for (var position = 0; position < ViewModel.Plans.Count; position++)
        {
            if (ViewModel.Plans[position].Plan.AnimeId == entry.Plan.AnimeId)
            {
                index = position;
                break;
            }
        }
        if (index >= 0 && VisiblePlanList.ContainerFromIndex(index) is FrameworkElement row)
            row.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
    }

    private void ClosePlanStack()
    {
        if (_capturingPlan)
        {
            ++_planOpenGeneration;
            return;
        }
        if (!PlanStackPopup.IsOpen || _closingPlanStack)
            return;
        _closingPlanStack = true;
        if (_planStackAnimation is null)
            AnimatePlanStack(closing: true);
    }

    private void CompletePlanStackClose()
    {
        if (_planHandoffImage is not { } image)
        {
            RestorePlanSurface();
            PlanStackPopup.IsOpen = false;
            return;
        }
        // 静态过渡画面留在原生浮层内，实际控件移回主窗口；没有第二套交互列表。
        Canvas.SetLeft(image, Canvas.GetLeft(PlanVisual));
        Canvas.SetTop(image, Canvas.GetTop(PlanVisual));
        PlanDismissCanvas.Children.Add(image);
        RestorePlanSurface();
        _planHandoffFrames = 0;
        CompositionTarget.Rendering += OnPlanHandoffRendering;
    }

    private void OnPlanHandoffRendering(object? sender, object e)
    {
        // Rendering 在呈现前通知；至少跨过一次渲染机会，而不是用固定毫秒延迟。
        if (++_planHandoffFrames < 2) return;
        CompositionTarget.Rendering -= OnPlanHandoffRendering;
        PlanStackPopup.IsOpen = false;
    }

    private void RestorePlanSurface()
    {
        if (_movingPlanSurface || PlanCard.Children.Contains(PlanVisual)) return;
        _movingPlanSurface = true;
        try
        {
            // 离树后再停止动画、复原变换，不能让重置后的展开状态露在浮层里。
            // 保留固定尺寸的透明浮层直到关闭，避免 Child=null 使原生窗口提前缩成零尺寸。
            PlanDismissCanvas.Children.Remove(PlanVisual);
            _planStackAnimation?.Stop();
            _planStackAnimation = null;
            if (_planPanel is not null) _planPanel.Expanded = false;
            PlanScroll.VerticalScrollMode = ScrollMode.Disabled;
            PlanScroll.IsHitTestVisible = true;
            ResetPlanMotion();
            // 先设置最终尺寸再入树，只做一次归位布局；中间过程不切换卡片模板。
            PlanVisual.ClearValue(Canvas.LeftProperty);
            PlanVisual.ClearValue(Canvas.TopProperty);
            PlanVisual.Width = double.NaN;
            PlanVisual.Height = double.NaN;
            PlanCard.Height = double.NaN;
            PlanCard.Children.Add(PlanVisual);
            PlanCard.UpdateLayout();
            PlanScroll.ChangeView(null, 0, null, disableAnimation: true);
        }
        finally
        {
            _movingPlanSurface = false;
        }
    }

    private void OnPlanStackClosed(object sender, object e)
    {
        ++_planOpenGeneration;
        CompositionTarget.Rendering -= OnPlanHandoffRendering;
        // 窗口变化、页面离开等直接关闭路径也必须归位；动画路径不重复迁移。
        RestorePlanSurface();
        if (_planHandoffImage is { } image)
        {
            PlanDismissCanvas.Children.Remove(image);
            image.Source = null;
            _planHandoffImage = null;
        }
        _closingPlanStack = false;
        _pendingPlan = null;
        DetachPlanWindowEvents();
        if (!_movingPlanSurface) UpdatePlanRowTemplate();
        if (IsLoaded && ViewModel.HasOverflowPlans)
            _planPanel?.Focus(FocusState.Programmatic);
    }

    private void DetachPlanWindowEvents()
    {
        if (_planActivationListener is not null)
        {
            _planActivationListener.InputActivationChanged -= OnPlanActivationChanged;
            _planActivationListener = null;
        }
        if (_planOwnerWindow is not null)
        {
            _planOwnerWindow.Changed -= OnPlanOwnerChanged;
            _planOwnerWindow = null;
        }
    }

    private void OnPlanStackKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            ClosePlanStack();
            e.Handled = true;
        }
    }

    private void OnPageScrollChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!_movingPlanSurface)
            PlanStackPopup.IsOpen = false;
    }
}
