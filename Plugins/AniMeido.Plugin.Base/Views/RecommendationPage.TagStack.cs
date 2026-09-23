
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class RecommendationPage
{
    private TodayPlanStackPanel? _tagPanel;
    private Storyboard? _tagAnimation;
    private Image? _tagHandoff;
    private AppWindow? _tagWindow;
    private InputActivationListener? _tagActivation;
    private bool _capturingTag;
    private bool _closingTag;
    private bool _movingTag;
    private int _tagGeneration;
    private int _tagHandoffFrames;
    private double _tagCollapsedHeight;
    private double _tagExpandedHeight;
    private double _tagChromeHeight;

    private void OnTagPanelLoaded(object sender, RoutedEventArgs e)
        => _tagPanel = (TodayPlanStackPanel)sender;

    private void OnTagPackEntered(object sender, PointerRoutedEventArgs e)
    {
        _overTagEntry = true;
        _tagCloseTimer.Stop();
        if (!TagStackPopup.IsOpen && ViewModel.SelectedTags.Count > 1)
            _tagOpenTimer.Start();
    }

    private void OnTagPackExited(object sender, PointerRoutedEventArgs e)
    {
        // 迁入 Popup 可能产生一次树切换退出事件；鼠标仍在同一张卡上时不收起。
        if (TagStackPopup.IsOpen)
        {
            var point = e.GetCurrentPoint(TagPackVisual).Position;
            if (point.X >= 0 && point.X < TagPackVisual.ActualWidth
                && point.Y >= 0 && point.Y < TagPackVisual.ActualHeight) return;
        }
        _overTagEntry = false;
        _tagOpenTimer.Stop();
        if (TagStackPopup.IsOpen && !_tagPinned) _tagCloseTimer.Start();
    }

    private async void OnTagOpenTick(object? sender, object e)
    {
        _tagOpenTimer.Stop();
        if (_overTagEntry) await TryOpenTagStackAsync();
    }

    private void OnTagCloseTick(object? sender, object e)
    {
        _tagCloseTimer.Stop();
        if (!_tagPinned && !_overTagEntry) CloseTagStack();
    }

    private async void OnTagPackTapped(object sender, TappedRoutedEventArgs e)
    {
        if (IsTagEditorControl(e.OriginalSource as DependencyObject)) return;
        if (!TagStackPopup.IsOpen && ViewModel.SelectedTags.Count <= 1) return;
        _tagPinned = true;
        _tagCloseTimer.Stop();
        if (TagStackPopup.IsOpen) CloseTagStack();
        else await TryOpenTagStackAsync();
        e.Handled = true;
    }

    private static bool IsTagEditorControl(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase) return true;
        return false;
    }

    private async void OnTagStackKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && TagStackPopup.IsOpen)
        {
            CloseTagStack();
            e.Handled = true;
        }
        else if (!TagStackPopup.IsOpen && ViewModel.SelectedTags.Count > 1
            && e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space
            && ReferenceEquals(e.OriginalSource, _tagPanel))
        {
            _tagPinned = true;
            e.Handled = true;
            await TryOpenTagStackAsync();
        }
    }

    private async Task TryOpenTagStackAsync()
    {
        try { await OpenTagStackAsync(); }
#pragma warning disable CA1031 // UI 边界将不可恢复的浮层错误回显到页面，避免 async void 终止进程。
        catch (Exception exception)
        {
            CloseTagStackImmediately();
            if (IsLoaded) ViewModel.ReportError(exception.Message);
        }
#pragma warning restore CA1031
    }

    private async Task OpenTagStackAsync()
    {
        if (!IsLoaded || _capturingTag || TagStackPopup.IsOpen || _tagPanel is null
            || ViewModel.SelectedTags.Count <= 1 || TagPackSurface.ActualWidth <= 0)
            return;

        var generation = ++_tagGeneration;
        var originalSize = new Size(TagPackVisual.ActualWidth, TagPackVisual.ActualHeight);
        var originalPosition = TagPackVisual.TransformToVisual(null).TransformPoint(new Point());
        RenderTargetBitmap? bitmap = new();
        _capturingTag = true;
        try { await bitmap.RenderAsync(TagPackVisual); }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or ArgumentException)
        {
            bitmap = null;
        }
        finally { _capturingTag = false; }
        if (!IsLoaded || generation != _tagGeneration || TagStackPopup.IsOpen
            || ViewModel.SelectedTags.Count <= 1
            || originalSize != new Size(TagPackVisual.ActualWidth, TagPackVisual.ActualHeight)
            || originalPosition != TagPackVisual.TransformToVisual(null).TransformPoint(new Point()))
            return;

        if (bitmap is { PixelWidth: > 0, PixelHeight: > 0 })
            _tagHandoff = new Image
            {
                Source = bitmap, Width = originalSize.Width, Height = originalSize.Height,
                Stretch = Stretch.Fill, IsHitTestVisible = false,
            };

        var origin = TagPackSurface.TransformToVisual(null).TransformPoint(new Point());
        _tagCollapsedHeight = TagPackSurface.ActualHeight;
        var width = TagPackSurface.ActualWidth;
        _tagChromeHeight = _tagCollapsedHeight - TagPackScroll.ActualHeight;
        var rowsHeight = _tagPanel.Children.Take(5).Sum(row => row.DesiredSize.Height);
        _tagExpandedHeight = Math.Max(_tagCollapsedHeight,
            Math.Min(AvailableTagHeight(origin), rowsHeight + _tagChromeHeight));

        TagStackPopup.HorizontalOffset = 0;
        TagStackPopup.VerticalOffset = 0;
        var popupOrigin = TagStackPopup.TransformToVisual(null).TransformPoint(new Point());
        TagStackPopup.HorizontalOffset = -popupOrigin.X;
        TagStackPopup.VerticalOffset = -popupOrigin.Y;
        TagDismissCanvas.Width = Math.Max(XamlRoot.Size.Width, origin.X + width);
        TagDismissCanvas.Height = Math.Max(XamlRoot.Size.Height, origin.Y + _tagExpandedHeight);
        Canvas.SetLeft(TagPackVisual, origin.X);
        Canvas.SetTop(TagPackVisual, origin.Y);

        _movingTag = true;
        try
        {
            TagPackHost.Height = _tagCollapsedHeight;
            TagPackVisual.Width = width;
            TagPackVisual.Height = _tagExpandedHeight;
            TagPackHost.Children.Remove(TagPackVisual);
            TagDismissCanvas.Children.Add(TagPackVisual);
            TagPackScroll.VerticalScrollMode = ScrollMode.Enabled;
            _tagPanel.Expanded = true;
            var popupSize = new Size(TagDismissCanvas.Width, TagDismissCanvas.Height);
            TagDismissCanvas.Measure(popupSize);
            TagDismissCanvas.Arrange(new Rect(new Point(), popupSize));
            TagDismissCanvas.UpdateLayout();
            AnimateTagStack(closing: false, startImmediately: false);
            TagStackPopup.IsOpen = true;
        }
        finally { _movingTag = false; }
    }

    private double AvailableTagHeight(Point origin)
    {
        var windowId = XamlRoot.ContentIslandEnvironment.AppWindowId;
        if (_tagWindow is null)
        {
            _tagWindow = AppWindow.GetFromWindowId(windowId);
            _tagWindow.Changed += OnTagWindowChanged;
            _tagActivation = InputActivationListener.GetForWindowId(windowId);
            _tagActivation.InputActivationChanged += OnTagActivationChanged;
        }
        var handle = Microsoft.UI.Win32Interop.GetWindowFromWindowId(windowId);
        var scale = XamlRoot.RasterizationScale;
        var point = new Windows.Graphics.PointInt32(
            (int)Math.Round(origin.X * scale), (int)Math.Round(origin.Y * scale));
        if (!TagClientToScreen(handle, ref point))
            return Math.Max(1, XamlRoot.Size.Height - origin.Y - 12);
        var area = DisplayArea.GetFromPoint(point, DisplayAreaFallback.Nearest).WorkArea;
        return Math.Max(1, (area.Y + area.Height - point.Y) / scale - 12);
    }

    [DllImport("user32.dll", EntryPoint = "ClientToScreen")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TagClientToScreen(nint window, ref Windows.Graphics.PointInt32 point);

    private void OnTagWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange) CloseTagStackImmediately();
    }

    private void OnTagActivationChanged(InputActivationListener sender, InputActivationListenerActivationChangedEventArgs args)
    {
        if (sender.State == InputActivationState.Deactivated) CloseTagStack();
    }

    private void OnTagStackOpened(object sender, object e)
    {
        _tagPanel?.Focus(FocusState.Programmatic);
        _tagAnimation?.Begin();
    }

    private void OnTagOutsidePressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsInsideTagStack(e.OriginalSource as DependencyObject)) return;
        e.Handled = true;
        CloseTagStack();
    }

    private void OnTagOutsideWheel(object sender, PointerRoutedEventArgs e)
    {
        if (IsInsideTagStack(e.OriginalSource as DependencyObject)) return;
        e.Handled = true;
        CloseTagStack();
    }

    private bool IsInsideTagStack(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, TagPackVisual)) return true;
        return false;
    }

    private void CloseTagStack()
    {
        if (_capturingTag)
        {
            ++_tagGeneration;
            return;
        }
        if (!TagStackPopup.IsOpen || _closingTag) return;
        _closingTag = true;
        if (_tagAnimation is null) AnimateTagStack(closing: true);
    }

    private void CloseTagStackImmediately()
    {
        ++_tagGeneration;
        _tagOpenTimer.Stop();
        _tagCloseTimer.Stop();
        if (TagStackPopup is null) return;
        RestoreTagStack();
        if (TagStackPopup.IsOpen) TagStackPopup.IsOpen = false;
        else if (_tagWindow is not null || _tagHandoff is not null)
            OnTagStackClosed(this, EventArgs.Empty);
    }

    private void AnimateTagStack(bool closing, bool startImmediately = true)
    {
        if (_tagPanel is null) return;
        _tagAnimation?.Stop();
        if (!new UISettings().AnimationsEnabled)
        {
            if (closing) CloseTagStackImmediately();
            return;
        }

        var animation = new Storyboard();
        var ratio = Math.Clamp(_tagCollapsedHeight / _tagExpandedHeight, 0.01, 1);
        var backdropScale = new ScaleTransform { ScaleY = closing ? 1 : ratio };
        TagPackBackdrop.RenderTransform = backdropScale;
        var clipScale = new ScaleTransform { ScaleY = closing ? 1 : ratio };
        TagPackSurface.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, TagPackVisual.Width, _tagExpandedHeight), Transform = clipScale,
        };
        AddTagMotion(animation, backdropScale, nameof(ScaleTransform.ScaleY), closing ? 1 : ratio, closing ? ratio : 1);
        AddTagMotion(animation, clipScale, nameof(ScaleTransform.ScaleY), closing ? 1 : ratio, closing ? ratio : 1);
        if (closing && TagPackScroll.VerticalOffset > 0)
        {
            var scrollReturn = new TranslateTransform();
            TagPackList.RenderTransform = scrollReturn;
            AddTagMotion(animation, scrollReturn, nameof(TranslateTransform.Y), 0, TagPackScroll.VerticalOffset);
        }

        var visibleCount = _tagPanel.VisibleCardCount;
        var fanOriginTop = _tagPanel.Children.Take(visibleCount - 1).Sum(child => child.DesiredSize.Height);
        var y = fanOriginTop;
        for (var index = visibleCount - 1; index < _tagPanel.Children.Count; index++)
        {
            var row = _tagPanel.Children[index];
            if (index >= visibleCount)
            {
                var offset = fanOriginTop - y;
                var angle = Math.Min(index - visibleCount + 1, 3) * _tagPanel.FanAngleStep;
                var transform = new CompositeTransform
                {
                    TranslateY = closing ? 0 : offset, Rotation = closing ? 0 : angle,
                };
                row.RenderTransform = transform;
                AddTagMotion(animation, transform, nameof(CompositeTransform.TranslateY), closing ? 0 : offset, closing ? offset : 0);
                AddTagMotion(animation, transform, nameof(CompositeTransform.Rotation), closing ? 0 : angle, closing ? angle : 0);
                if (TodayPlanStackPanel.GetCardFace(row) is { } face)
                    AddTagMotion(animation, face, nameof(Opacity), closing ? 1 : 0, closing ? 0 : 1);
                if (index >= visibleCount + 3)
                    AddTagMotion(animation, row, nameof(Opacity), closing ? 1 : 0, closing ? 0 : 1);
            }
            y += row.DesiredSize.Height;
        }
        TagPackScroll.IsHitTestVisible = false;
        _tagPanel.IsAnimating = true;
        _tagAnimation = animation;
        animation.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_tagAnimation, animation)) return;
            if (closing)
            {
                CompleteTagStackClose();
                return;
            }
            animation.Stop();
            _tagAnimation = null;
            ResetTagMotion();
            TagPackScroll.IsHitTestVisible = true;
            UpdateTagStackSize();
            if (_closingTag) AnimateTagStack(closing: true);
        };
        if (startImmediately) animation.Begin();
    }

    private static void AddTagMotion(Storyboard storyboard, DependencyObject target, string property, double from, double to)
    {
        var animation = new DoubleAnimation
        {
            From = from, To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(360)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = false,
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    private void ResetTagMotion()
    {
        TagPackSurface.Clip = null;
        TagPackBackdrop.RenderTransform = null;
        TagPackList.RenderTransform = null;
        if (_tagPanel is null) return;
        _tagPanel.IsAnimating = false;
        foreach (var row in _tagPanel.Children)
        {
            row.RenderTransform = new CompositeTransform();
            row.Opacity = 1;
        }
        _tagPanel.InvalidateArrange();
    }

    private void CompleteTagStackClose()
    {
        if (_tagHandoff is not { } image)
        {
            RestoreTagStack();
            TagStackPopup.IsOpen = false;
            return;
        }
        Canvas.SetLeft(image, Canvas.GetLeft(TagPackVisual));
        Canvas.SetTop(image, Canvas.GetTop(TagPackVisual));
        TagDismissCanvas.Children.Add(image);
        RestoreTagStack();
        _tagHandoffFrames = 0;
        CompositionTarget.Rendering += OnTagHandoffRendering;
    }

    private void OnTagHandoffRendering(object? sender, object e)
    {
        if (++_tagHandoffFrames < 2) return;
        CompositionTarget.Rendering -= OnTagHandoffRendering;
        TagStackPopup.IsOpen = false;
    }

    private void RestoreTagStack()
    {
        if (_movingTag || TagPackHost.Children.Contains(TagPackVisual)) return;
        _movingTag = true;
        try
        {
            TagDismissCanvas.Children.Remove(TagPackVisual);
            _tagAnimation?.Stop();
            _tagAnimation = null;
            if (_tagPanel is not null) _tagPanel.Expanded = false;
            TagPackScroll.VerticalScrollMode = ScrollMode.Disabled;
            TagPackScroll.IsHitTestVisible = true;
            ResetTagMotion();
            TagPackVisual.ClearValue(Canvas.LeftProperty);
            TagPackVisual.ClearValue(Canvas.TopProperty);
            TagPackVisual.Width = double.NaN;
            TagPackVisual.Height = double.NaN;
            TagPackHost.Height = double.NaN;
            TagPackHost.Children.Add(TagPackVisual);
            TagPackHost.UpdateLayout();
            TagPackScroll.ChangeView(null, 0, null, disableAnimation: true);
        }
        finally { _movingTag = false; }
    }

    private void OnTagStackClosed(object sender, object e)
    {
        ++_tagGeneration;
        CompositionTarget.Rendering -= OnTagHandoffRendering;
        RestoreTagStack();
        if (_tagHandoff is { } image)
        {
            TagDismissCanvas.Children.Remove(image);
            image.Source = null;
            _tagHandoff = null;
        }
        _closingTag = false;
        _tagPinned = false;
        _overTagEntry = false;
        if (_tagActivation is not null)
        {
            _tagActivation.InputActivationChanged -= OnTagActivationChanged;
            _tagActivation = null;
        }
        if (_tagWindow is not null)
        {
            _tagWindow.Changed -= OnTagWindowChanged;
            _tagWindow = null;
        }
    }

    private void UpdateTagStackSize()
    {
        if (!TagStackPopup.IsOpen || _tagPanel is null || _tagAnimation is not null) return;
        TagPackList.UpdateLayout();
        var origin = TagPackVisual.TransformToVisual(null).TransformPoint(new Point());
        var rowsHeight = _tagPanel.Children.Take(5).Sum(row => row.DesiredSize.Height);
        _tagExpandedHeight = Math.Max(_tagCollapsedHeight,
            Math.Min(AvailableTagHeight(origin), rowsHeight + _tagChromeHeight));
        TagPackVisual.Height = _tagExpandedHeight;
        TagDismissCanvas.Height = Math.Max(XamlRoot.Size.Height, origin.Y + _tagExpandedHeight);
    }
}
