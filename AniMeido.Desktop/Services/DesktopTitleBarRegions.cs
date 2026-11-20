using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics;

namespace AniMeido.App.Services;

internal sealed class DesktopTitleBarRegions : IDisposable
{
    private readonly FrameworkElement _root;
    private readonly FrameworkElement _button;
    private readonly Action<RectInt32[]> _publish;
    private XamlRoot? _xamlRoot;
    private RectInt32? _last;
    private bool _queued;
    private bool _disposed;

    internal DesktopTitleBarRegions(FrameworkElement root, FrameworkElement button, Action<RectInt32[]> publish)
    {
        _root = root; _button = button; _publish = publish;
        root.Loaded += OnLoaded;
        root.Unloaded += OnUnloaded;
        root.SizeChanged += OnSizeChanged;
        button.Loaded += OnLoaded;
        button.SizeChanged += OnSizeChanged;
        if (root.IsLoaded) OnLoaded(root, new RoutedEventArgs());
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_xamlRoot is null && _root.XamlRoot is { } root)
        {
            _xamlRoot = root;
            root.Changed += OnRootChanged;
        }
        QueueUpdate();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (_xamlRoot is { } root) root.Changed -= OnRootChanged;
        _xamlRoot = null;
        _last = null;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args) => QueueUpdate();
    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => QueueUpdate();

    private void QueueUpdate()
    {
        if (_queued || _disposed) return;
        _queued = true;
        if (!_root.DispatcherQueue.TryEnqueue(() =>
        {
            _queued = false;
            if (_disposed || !_button.IsLoaded || _button.ActualWidth <= 0 || _button.ActualHeight <= 0) return;
            var bounds = _button.TransformToVisual(_root).TransformBounds(new Rect(0, 0, _button.ActualWidth, _button.ActualHeight));
            var physical = ToPhysicalBounds(bounds, _root.XamlRoot?.RasterizationScale ?? 1);
            if (_last is { } previous && previous.Equals(physical)) return;
            _last = physical;
            _publish([physical]);
        })) _queued = false;
    }

    // NonClientRegionKind 的矩形采用物理客户区坐标；XAML 布局坐标在这里缩放一次。
    internal static RectInt32 ToPhysicalBounds(Rect bounds, double scale)
    {
        var left = (int)Math.Floor(bounds.Left * scale);
        var top = (int)Math.Floor(bounds.Top * scale);
        return new(left, top, (int)Math.Ceiling(bounds.Right * scale) - left, (int)Math.Ceiling(bounds.Bottom * scale) - top);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        OnUnloaded(_root, new RoutedEventArgs());
        _root.Loaded -= OnLoaded; _root.Unloaded -= OnUnloaded; _root.SizeChanged -= OnSizeChanged;
        _button.Loaded -= OnLoaded; _button.SizeChanged -= OnSizeChanged;
    }
}
