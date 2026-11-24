using AniMeido.App.Services;
using AniMeido.Plugin.Base.Views.Controls;
using AniMeido.Plugin.Base.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using System.Diagnostics;

namespace AniMeido.App;

public sealed partial class MainWindow
{
    private Storyboard? _pageMotion;
    private DesktopPageLoadSurface? _waitingSurface;
    private FrameworkElement? _movingContent;
    private Frame CurrentFrame => _navigation.ActiveFrame ?? ContentFrame;
    private Frame? _outgoing;
    private Page? _arranging;
    private DispatcherQueueTimer? _deadline;
    private AnimeDetailPage? _expanding;
    private bool _alreadyVisible;
    private Frame? _unfoldingPaper;
    private long _paperStart;

    private Frame MountPage(Frame active, Page page, DesktopNavigationKind kind)
    {
        var other = ReferenceEquals(active, ContentFrame) ? AlternateFrame : ContentFrame;
        var stillCovered = _outgoing is not null && (active.Opacity < .5 || _expanding is not null
            || _unfoldingPaper == active && active.Clip.Rect.Width < active.ActualWidth / 2);
        CancelSignals();
        StopAnimation();
        _outgoing = null;
        bool retain = active.Content is Page old && active.IsLoaded
            && (_theme.CurrentAppearance.VisualStyle == DesktopVisualStyle.Paper
                || kind == DesktopNavigationKind.TopLevel || old is PastSeasonPage && page is AnimeDetailPage
                || old is AnimeDetailPage && page is PastSeasonPage && kind == DesktopNavigationKind.Back)
            && Application.Current.Resources.TryGetValue("DesktopMotionEnabled", out var enabled) && enabled is true;
        // 快速返回尚在下层的实例时，直接恢复它，不卸载、重挂。
        _alreadyVisible = ReferenceEquals(other.Content, page) && stillCovered;
        if (_alreadyVisible) retain = false;
        var source = stillCovered && !ReferenceEquals(other.Content, page) ? other : active;
        var next = retain ? (ReferenceEquals(source, active) ? other : active)
            : ReferenceEquals(other.Content, page) ? other : active;
        var lower = ReferenceEquals(next, active) ? other : active;
        if (!ReferenceEquals(next.Content, page)) next.Content = page;
        next.Visibility = Visibility.Visible;
        next.IsEnabled = true;
        next.IsHitTestVisible = !retain;
        next.Opacity = retain ? 0 : 1;
        Canvas.SetZIndex(next, 1);
        Canvas.SetZIndex(lower, 0);
        if (retain)
        {
            lower.Opacity = 1;
            lower.IsHitTestVisible = lower.IsEnabled = false;
            _outgoing = lower;
        }
        else Empty(lower);
        return next;
    }

    private static void Empty(Frame frame)
    {
        frame.Visibility = Visibility.Collapsed;
        frame.Content = null;
        frame.Opacity = 1;
        frame.IsHitTestVisible = frame.IsEnabled = true;
    }

    private void OnWorkspaceSizeChanged(object sender, SizeChangedEventArgs args)
        => WorkspaceClip.Rect = new Rect(0, 0, args.NewSize.Width, args.NewSize.Height);

    private void StartPageMotion()
    {
        if (_alreadyVisible) return;
        if (_outgoing is not null && CurrentFrame.Content is Page incoming)
        {
            _arranging = incoming;
            incoming.LayoutUpdated += OnIncomingLayout;
            if (incoming is IDesktopLoadingPage loading)
            {
                _waitingSurface = loading.LoadSurface;
                _waitingSurface.Ready += OnPageContentReady;
            }
            SetDeadline(180, () => EnterRetainedPage(timedOut: true));
            return;
        }
        if (!_navigation.IsFirstNavigationCompleted || !CurrentFrame.IsLoaded
            || !Application.Current.Resources.TryGetValue("DesktopFluentPageMotionEnabled", out var enabled) || enabled is not true)
            return;

        // 加载页先显示自己的提示。只在本次导航等待正文，页内刷新不会重播入场。
        if (CurrentFrame.Content is IDesktopLoadingPage page)
        {
            _waitingSurface = page.LoadSurface;
            _waitingSurface.Ready += OnPageContentReady;
            OnPageContentReady(_waitingSurface, EventArgs.Empty);
        }
        else PlayPageMotion(CurrentFrame);
    }

    private void OnIncomingLayout(object? sender, object args) => EnterRetainedPage(timedOut: false);

    private void EnterRetainedPage(bool timedOut)
    {
        if (_arranging is not { } page || !ReferenceEquals(page, CurrentFrame.Content)) return;
        if (!timedOut && (!page.IsLoaded || page.ActualWidth <= 0 || page.ActualHeight <= 0)) return;
        var paper = _theme.CurrentAppearance.VisualStyle == DesktopVisualStyle.Paper;
        var card = page as AnimeDetailPage;
        var expanding = paper && card?.IsPaperCardOpening == true && _outgoing?.Content is PastSeasonPage;
        var shrinking = paper && page is PastSeasonPage { IsPaperCardReturning: true }
            && _navigation.LastNavigationKind == DesktopNavigationKind.Back && _outgoing?.Content is AnimeDetailPage;
        if (!timedOut && !expanding && !shrinking && _waitingSurface is { ReadyTarget: null }) return;
        StopDeadline();
        page.LayoutUpdated -= OnIncomingLayout;
        _arranging = null;
        if (expanding || shrinking || _waitingSurface?.ReadyTarget is not null) StopWaiting();
        CurrentFrame.IsHitTestVisible = true;
        if (expanding)
        {
            // 详情自己的纸面从原卡铺开，不叠加第二次入场。
            _expanding = card;
            card!.PaperCardExpanded += OnCardExpanded;
            CurrentFrame.Opacity = 1;
            SetDeadline(550, FinishRetainedPage);
        }
        else if (shrinking)
        {
            // 列表已铺好，只有整卡收回。整页再淡入会让旧详情透过列表。
            CurrentFrame.Opacity = 1;
            FinishRetainedPage();
        }
        else if (paper && _navigation.LastNavigationKind == DesktopNavigationKind.TopLevel)
            UnfoldPaper(CurrentFrame);
        else PlayPageMotion(CurrentFrame, opacityOnly: !paper && timedOut && _navigation.LastNavigationKind != DesktopNavigationKind.TopLevel);
    }

    private void UnfoldPaper(Frame frame)
    {
        // 纸面后的正文已经排好，只移动可见边界；旧页和新页都不做半透明混合。
        _unfoldingPaper = frame;
        frame.Clip = new RectangleGeometry { Rect = new Rect(0, 0, 0, frame.ActualHeight) };
        frame.Background = (Brush)Application.Current.Resources["DesktopPageBrush"];
        frame.Opacity = 1;
        _paperStart = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnPaperRendering;
        // 窗口最小化时帧回调可能暂停，旧页仍要按时释放。
        SetDeadline(400, FinishPaper);
    }

    private void OnPaperRendering(object? sender, object args)
    {
        if (_unfoldingPaper is not { } frame) return;
        var t = Stopwatch.GetElapsedTime(_paperStart).TotalMilliseconds / 320;
        if (t >= 1) { FinishPaper(); return; }
        frame.Clip.Rect = new Rect(0, 0, frame.ActualWidth * t * t * (3 - 2 * t), frame.ActualHeight);
    }

    private void FinishPaper()
    {
        StopAnimation();
        FinishRetainedPage();
    }

    private void OnCardExpanded(object? sender, EventArgs args) => FinishRetainedPage();

    private void SetDeadline(int milliseconds, Action elapsed)
    {
        StopDeadline();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => { if (ReferenceEquals(_deadline, timer)) elapsed(); };
        _deadline = timer;
        timer.Start();
    }

    private void StopDeadline() { _deadline?.Stop(); _deadline = null; }

    private void FinishRetainedPage()
    {
        StopDeadline();
        if (_expanding is { } card) card.PaperCardExpanded -= OnCardExpanded;
        _expanding = null;
        if (_outgoing is { } frame) Empty(frame);
        _outgoing = null;
    }

    private void OnPageContentReady(object? sender, EventArgs args)
    {
        if (_waitingSurface is not { ReadyTarget: { } target } surface || !ReferenceEquals(surface, sender)
            || CurrentFrame.Content is not IDesktopLoadingPage page || !ReferenceEquals(surface, page.LoadSurface)) return;
        if (_arranging is not null) { EnterRetainedPage(timedOut: false); return; }
        StopWaiting();
        if (_outgoing is not null) return; // 新正文已包含在加载页的淡入中。
        PlayPageMotion(target);
    }

    private void PlayPageMotion(FrameworkElement target, bool opacityOnly = false)
    {
        var style = _theme.CurrentAppearance.VisualStyle;
        var kind = _navigation.LastNavigationKind;
        var transform = new CompositeTransform();
        var paper = style == DesktopVisualStyle.Paper;
        if (paper && kind != DesktopNavigationKind.TopLevel)
            transform.TranslateX = kind == DesktopNavigationKind.Back ? -32 : 32;
        else if (!opacityOnly && style == DesktopVisualStyle.Fluent10)
            transform.ScaleX = transform.ScaleY = kind == DesktopNavigationKind.Back ? 1.06 : .94;
        else if (!opacityOnly && style == DesktopVisualStyle.Fluent11)
        {
            if (kind == DesktopNavigationKind.TopLevel) transform.TranslateY = 38;
            else transform.TranslateX = kind == DesktopNavigationKind.Forward ? 58 : -58;
        }

        // Uno 在下一个时钟周期才应用第一个关键帧，先设好起点以免新页先闪现在终点。
        _movingContent = target;
        target.RenderTransformOrigin = new Point(.5, .5);
        target.RenderTransform = transform;
        target.Opacity = 0;
        var slide = new Storyboard();
        var end = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(paper ? 320 : opacityOnly ? 170 : kind == DesktopNavigationKind.TopLevel ? 280 : 260));
        Track(transform, nameof(CompositeTransform.TranslateX), transform.TranslateX, 0);
        Track(transform, nameof(CompositeTransform.TranslateY), transform.TranslateY, 0);
        Track(transform, nameof(CompositeTransform.ScaleX), transform.ScaleX, 1);
        Track(transform, nameof(CompositeTransform.ScaleY), transform.ScaleY, 1);
        Track(target, nameof(UIElement.Opacity), 0, 1);
        _pageMotion = slide;
        slide.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_pageMotion, slide)) return;
            StopAnimation();
            FinishRetainedPage();
        };
        slide.Begin();

        void Track(DependencyObject target, string property, double from, double to)
        {
            if (from == to) return;
            var track = new DoubleAnimationUsingKeyFrames();
            track.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = from });
            track.KeyFrames.Add(new SplineDoubleKeyFrame
            {
                KeyTime = end, Value = to,
                KeySpline = new KeySpline { ControlPoint1 = paper ? new Point(.25, .06) : new Point(.12, .68),
                    ControlPoint2 = paper ? new Point(.15, 1) : new Point(.18, 1) },
            });
            Storyboard.SetTarget(track, target);
            Storyboard.SetTargetProperty(track, property);
            slide.Children.Add(track);
        }
    }

    private void StopWaiting()
    {
        if (_waitingSurface is { } surface) surface.Ready -= OnPageContentReady;
        _waitingSurface = null;
    }

    private void CancelSignals()
    {
        StopWaiting();
        StopDeadline();
        if (_arranging is { } page) page.LayoutUpdated -= OnIncomingLayout;
        _arranging = null;
        if (_expanding is { } card) card.PaperCardExpanded -= OnCardExpanded;
        _expanding = null;
    }

    private void StopPageMotion()
    {
        CancelSignals();
        StopAnimation();
        CurrentFrame.Opacity = 1;
        CurrentFrame.IsHitTestVisible = true;
        FinishRetainedPage();
    }

    private void StopAnimation()
    {
        if (_unfoldingPaper is { } frame)
        {
            CompositionTarget.Rendering -= OnPaperRendering;
            _unfoldingPaper = null;
            frame.ClearValue(UIElement.ClipProperty);
            frame.ClearValue(Control.BackgroundProperty);
        }
        if (_pageMotion is not { } slide || _movingContent is not { } target) return;
        _pageMotion = null;
        _movingContent = null;
        slide.Stop();
        target.ClearValue(UIElement.RenderTransformProperty);
        target.ClearValue(UIElement.OpacityProperty);
    }
}
