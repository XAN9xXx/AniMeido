using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace AniMeido.Plugin.Base.Views.Controls;

// 计划浮层移动原控件；关闭时立即归位，不持有窗口或截图。
internal sealed class DesktopDeckOverlay
{
    private readonly Grid _host;
    private readonly FrameworkElement _surface;
    private readonly ScrollViewer _scroll;
    private readonly Popup _popup;
    private readonly Canvas _canvas;
    private readonly Func<TodayPlanStackPanel?> _panel;
    private readonly Control? _returnFocus;
    private XamlRoot? _root;
    private double _hostHeight;
    private Size _rootSize;
    private double _scale;
    private bool _closeQueued;

    internal DesktopDeckOverlay(Grid host, FrameworkElement surface, ScrollViewer scroll,
        Popup popup, Canvas canvas, Func<TodayPlanStackPanel?> panel, Control? returnFocus = null)
    {
        _host = host;
        _surface = surface;
        _scroll = scroll;
        _popup = popup;
        _canvas = canvas;
        _panel = panel;
        _returnFocus = returnFocus;
        _popup.IsLightDismissEnabled = true;
        // Uno 延后派发 Closed；上一轮的通知到达时，新一轮可能已经打开。
        _popup.Closed += (_, _) => { if (!_popup.IsOpen) Restore(); };
        _surface.KeyDown += OnKeyDown;
        _canvas.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _canvas.PointerPressed += (_, args) =>
        {
            if (args.OriginalSource is DependencyObject source && DismissFrom(source)) args.Handled = true;
        };
        _canvas.PointerWheelChanged += (_, args) =>
        {
            if (args.OriginalSource is DependencyObject source && DismissFrom(source)) args.Handled = true;
        };
    }

    internal bool IsOpen => _popup.IsOpen;
    internal event EventHandler? Closed;

    internal bool DismissFrom(DependencyObject source)
    {
        if (!IsOpen || IsWithin(source, _surface)) return false;
        Close();
        return true;
    }

    internal bool Open()
    {
        if (IsOpen || !_host.IsLoaded || _host.XamlRoot is not { } root
            || _panel() is not { } panel || _surface.ActualWidth <= 0) return false;

        var origin = _surface.TransformToVisual(root.Content).TransformPoint(new Point());
        var width = Math.Min(_surface.ActualWidth, Math.Max(1, root.Size.Width - 16));
        var top = Math.Max(8, Math.Min(origin.Y, root.Size.Height - 200));
        var height = Math.Max(1, root.Size.Height - top - 12);
        _hostHeight = _host.Height;
        _host.Height = _host.ActualHeight;
        _surface.Width = width;
        _surface.MaxHeight = height;
        _canvas.Width = root.Size.Width;
        _canvas.Height = root.Size.Height;
        Canvas.SetLeft(_surface, Math.Clamp(origin.X, 8, Math.Max(8, root.Size.Width - width - 8)));
        Canvas.SetTop(_surface, top);
        _host.Children.Remove(_surface);
        _canvas.Children.Add(_surface);
        panel.Expanded = true;
        _scroll.VerticalScrollMode = ScrollMode.Enabled;
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _root = root;
        _rootSize = root.Size;
        _scale = root.RasterizationScale;
        root.Changed += OnRootChanged;
        _popup.XamlRoot = root;
        var offset = _popup.TransformToVisual(root.Content).TransformPoint(new Point());
        _popup.HorizontalOffset = -offset.X;
        _popup.VerticalOffset = -offset.Y;
        _popup.IsOpen = true;
        return true;
    }

    internal void Close()
    {
        if (_popup.IsOpen) _popup.IsOpen = false;
        Restore();
    }

    private void Restore()
    {
        if (_root is not { } root) return;
        _root = null;
        root.Changed -= OnRootChanged;
        var restoreFocus = FocusManager.GetFocusedElement(root) is DependencyObject focused
            && IsWithin(focused, _surface);
        _canvas.Children.Remove(_surface);
        _surface.Width = double.NaN;
        _surface.MaxHeight = double.PositiveInfinity;
        _host.Children.Add(_surface);
        _host.Height = _hostHeight;
        if (_panel() is { } panel) panel.Expanded = false;
        _scroll.VerticalScrollMode = ScrollMode.Disabled;
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        Closed?.Invoke(this, EventArgs.Empty);
        if (restoreFocus && _returnFocus is { IsLoaded: true })
            _returnFocus.Focus(FocusState.Programmatic);
    }

    private static bool IsWithin(DependencyObject child, DependencyObject parent)
    {
        for (DependencyObject? current = child; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, parent)) return true;
        return false;
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (_closeQueued || !ReferenceEquals(sender, _root)
            || sender.Size == _rootSize && sender.RasterizationScale == _scale) return;
        // 根节点通知可能来自正在进行的布局，等当前布局完成后再迁移控件。
        _closeQueued = true;
        if (!_host.DispatcherQueue.TryEnqueue(() =>
        {
            _closeQueued = false;
            if (ReferenceEquals(sender, _root)
                && (sender.Size != _rootSize || sender.RasterizationScale != _scale)) Close();
        })) _closeQueued = false;
    }
    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Escape || !IsOpen) return;
        args.Handled = true;
        Close();
    }
}
