using System.Runtime.InteropServices;
using AniMeido.Plugin.Base.ViewModels;
using AniMeido.Plugin.Base.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class TodayPage
{
    private Storyboard? _planStackAnimation;
    private TodayPlanStackPanel? _planPanel;
    private bool _closingPlanStack;
    private bool _movingPlanSurface;
    private double _collapsedStackHeight;
    private double _expandedStackHeight;
    private AppWindow? _planOwnerWindow;
    private readonly Grid _planPopupHost = new();
    private readonly Border _planBackdrop = new() { IsHitTestVisible = false };
    private Brush? _planBackground;
    private Brush? _planStroke;

    private void OnPlanPanelLoaded(object sender, RoutedEventArgs e)
        => _planPanel = (TodayPlanStackPanel)sender;

    private void OnPlanDeckTapped(object sender, TappedRoutedEventArgs e)
    {
        if (!PlanStackPopup.IsOpen && ViewModel.HasOverflowPlans && ReferenceEquals(e.OriginalSource, sender))
        {
            OpenPlanStack();
            e.Handled = true;
        }
    }

    private void OnPlanDeckKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!PlanStackPopup.IsOpen && ViewModel.HasOverflowPlans
            && ReferenceEquals(e.OriginalSource, sender)
            && e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
        {
            OpenPlanStack();
            e.Handled = true;
        }
    }

    private void OpenPlanStack()
    {
        if (!IsLoaded || PlanStackPopup.IsOpen || _planPanel is null || PlanSurface.ActualWidth <= 0)
            return;

        var origin = PlanSurface.TransformToVisual(null).TransformPoint(new Point());
        _collapsedStackHeight = PlanSurface.ActualHeight;
        var width = PlanSurface.ActualWidth;
        var availableHeight = AvailablePlanStackHeight(origin);
        var chromeHeight = _collapsedStackHeight - PlanScroll.ActualHeight;
        _expandedStackHeight = Math.Max(1, Math.Min(availableHeight,
            Math.Max(_collapsedStackHeight, _planPanel.ExpandedHeight + chromeHeight)));

        PlanStackPopup.HorizontalOffset = 0;
        PlanStackPopup.VerticalOffset = 0;
        var popupOrigin = PlanStackPopup.TransformToVisual(null).TransformPoint(new Point());
        PlanStackPopup.HorizontalOffset = origin.X - popupOrigin.X;
        PlanStackPopup.VerticalOffset = origin.Y - popupOrigin.Y;

        // 仅留下透明尺寸占位，底板、标题、卡片和通知栏一起迁移，原位没有副本。
        _movingPlanSurface = true;
        PlanCard.Height = _collapsedStackHeight;
        PlanSurface.Width = width;
        PlanSurface.Height = _expandedStackHeight;
        _planBackground = PlanSurface.Background;
        _planStroke = PlanSurface.BorderBrush;
        _planBackdrop.Background = _planBackground;
        _planBackdrop.BorderBrush = _planStroke;
        _planBackdrop.BorderThickness = PlanSurface.BorderThickness;
        _planBackdrop.CornerRadius = PlanSurface.CornerRadius;
        _planPopupHost.Width = width;
        _planPopupHost.Height = _expandedStackHeight;
        _planPopupHost.Children.Add(_planBackdrop);
        MovePlanSurface(() =>
        {
            PlanCard.Children.Remove(PlanSurface);
            _planPopupHost.Children.Add(PlanSurface);
            PlanStackPopup.Child = _planPopupHost;
        });
        PlanScroll.VerticalScrollMode = ScrollMode.Enabled;
        PlanStackPopup.IsOpen = true;
        _movingPlanSurface = false;
    }

    private double AvailablePlanStackHeight(Point origin)
    {
        var windowId = XamlRoot.ContentIslandEnvironment.AppWindowId;
        _planOwnerWindow = AppWindow.GetFromWindowId(windowId);
        _planOwnerWindow.Changed += OnPlanOwnerChanged;
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

    [DllImport("user32.dll", EntryPoint = "ClientToScreen")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlanClientToScreen(nint window, ref Windows.Graphics.PointInt32 point);

    private void OnPlanStackOpened(object sender, object e)
    {
        _planPanel?.Focus(FocusState.Programmatic);
        AnimatePlanStack(closing: false);
    }

    private void AnimatePlanStack(bool closing)
    {
        if (_planPanel is null) return;
        _planStackAnimation?.Stop();
        // 浮层和列表只布局一次；逐帧工作仅为渲染变换，不再动画 Height 或自定义布局属性。
        _planPanel.Expanded = true;
        PlanSurface.UpdateLayout();
        if (!new UISettings().AnimationsEnabled)
        {
            if (closing) PlanStackPopup.IsOpen = false;
            return;
        }
        var animation = new Storyboard();
        var ratio = Math.Clamp(_collapsedStackHeight / _expandedStackHeight, 0.01, 1);
        var scale = new ScaleTransform { ScaleY = closing ? 1 : ratio };
        _planBackdrop.RenderTransform = scale;
        _planBackdrop.Visibility = Visibility.Visible;
        PlanSurface.Background = null;
        PlanSurface.BorderBrush = null;
        var clipScale = new ScaleTransform { ScaleY = closing ? 1 : ratio };
        PlanSurface.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, PlanSurface.Width, _expandedStackHeight), Transform = clipScale,
        };
        var footer = new TranslateTransform { Y = closing ? 0 : _collapsedStackHeight - _expandedStackHeight };
        PlanFooter.RenderTransform = footer;
        AddStackMotion(animation, scale, nameof(ScaleTransform.ScaleY), closing ? 1 : ratio, closing ? ratio : 1);
        AddStackMotion(animation, clipScale, nameof(ScaleTransform.ScaleY), closing ? 1 : ratio, closing ? ratio : 1);
        AddStackMotion(animation, footer, nameof(TranslateTransform.Y),
            closing ? 0 : _collapsedStackHeight - _expandedStackHeight,
            closing ? _collapsedStackHeight - _expandedStackHeight : 0);
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
        _planStackAnimation = animation;
        animation.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_planStackAnimation, animation)) return;
            animation.Stop();
            _planStackAnimation = null;
            ResetPlanMotion();
            PlanScroll.IsHitTestVisible = true;
            if (closing) PlanStackPopup.IsOpen = false;
            else if (_closingPlanStack) AnimatePlanStack(closing: true);
            else if (_pendingPlan is { } entry) ScrollToPlan(entry);
        };
        animation.Begin();
    }

    private void ResetPlanMotion()
    {
        PlanSurface.Clip = null;
        PlanSurface.Background = _planBackground;
        PlanSurface.BorderBrush = _planStroke;
        _planBackdrop.Visibility = Visibility.Collapsed;
        PlanFooter.RenderTransform = null;
        if (_planPanel is null) return;
        foreach (var row in _planPanel.Children)
        {
            row.RenderTransform = new CompositeTransform();
            row.Opacity = 1;
        }
        _planPanel.InvalidateArrange();
    }

    private void MovePlanSurface(Action move)
    {
        var covers = PlanImages(PlanSurface)
            .Select(image => (Image: image, Context: image.DataContext, Source: image.Source)).ToArray();
        move();
        // 跨 XamlRoot 的卸载/加载事件可能交错。等待它们结束，再恢复请求与原位图。
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!IsLoaded) return;
            foreach (var cover in covers)
            {
                if (!cover.Image.IsLoaded || !ReferenceEquals(cover.Context, cover.Image.DataContext)) continue;
                ManagedImageLoader.Cancel(cover.Image, clearSource: false);
                ConfigureCover(cover.Image);
                if (cover.Source is not null) cover.Image.Source = cover.Source;
            }
        });
    }

    private static IEnumerable<Image> PlanImages(DependencyObject parent)
    {
        if (parent is Image image) yield return image;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var child in PlanImages(VisualTreeHelper.GetChild(parent, index)))
                yield return child;
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
        if (VisiblePlanList.ContainerFromIndex(ViewModel.Plans.IndexOf(entry)) is FrameworkElement row)
            row.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
    }

    private void ClosePlanStack()
    {
        if (!PlanStackPopup.IsOpen || _closingPlanStack)
            return;
        _closingPlanStack = true;
        PlanScroll.ChangeView(null, 0, null, disableAnimation: true);
        if (_planStackAnimation is null)
            AnimatePlanStack(closing: true);
    }

    private void OnPlanStackClosed(object sender, object e)
    {
        _movingPlanSurface = true;
        _planStackAnimation?.Stop();
        _planStackAnimation = null;
        _closingPlanStack = false;
        _pendingPlan = null;
        if (_planOwnerWindow is not null)
        {
            _planOwnerWindow.Changed -= OnPlanOwnerChanged;
            _planOwnerWindow = null;
        }
        if (_planPanel is not null)
            _planPanel.Expanded = false;
        PlanScroll.ChangeView(null, 0, null, disableAnimation: true);
        PlanScroll.VerticalScrollMode = ScrollMode.Disabled;
        PlanScroll.IsHitTestVisible = true;
        ResetPlanMotion();
        MovePlanSurface(() =>
        {
            _planPopupHost.Children.Remove(PlanSurface);
            _planPopupHost.Children.Clear();
            PlanStackPopup.Child = null;
            if (!PlanCard.Children.Contains(PlanSurface)) PlanCard.Children.Add(PlanSurface);
        });
        PlanSurface.Width = double.NaN;
        PlanSurface.Height = double.NaN;
        PlanCard.Height = double.NaN;
        _movingPlanSurface = false;
        if (IsLoaded && ViewModel.HasOverflowPlans)
            _planPanel?.Focus(FocusState.Programmatic);
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
