using AniMeido.Plugin.Base.Services;
using Microsoft.UI.Xaml;

namespace AniMeido.App.Services;

/// <summary>
/// App 层主窗口拖放接线适配器。
/// Base 的拖放服务负责业务；此类型拥有主窗口元素的事件订阅和宿主注册。
/// </summary>
internal sealed class MainWindowDragDropAdapter : IDisposable
{
    private readonly Lazy<DragDropService> _dragDropService;
    private readonly AnimeCardDropHost _dropHost = new();
    private FrameworkElement? _pageHost;
    private UIElement? _rootGrid;
    private UIElement? _mainNavigationView;
    private bool _disposed;

    public MainWindowDragDropAdapter(DragDropService dragDropService)
        : this(() => dragDropService)
    {
        ArgumentNullException.ThrowIfNull(dragDropService);
    }

    internal MainWindowDragDropAdapter(Func<DragDropService> createService)
    {
        _dragDropService = new Lazy<DragDropService>(createService);
    }

    public void Attach(
        UIElement rootGrid,
        UIElement mainNavigationView,
        FrameworkElement pageHost)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_pageHost is not null)
        {
            throw new InvalidOperationException("主窗口拖放适配器只能绑定一次。");
        }

        ArgumentNullException.ThrowIfNull(rootGrid);
        ArgumentNullException.ThrowIfNull(mainNavigationView);
        ArgumentNullException.ThrowIfNull(pageHost);

        _rootGrid = rootGrid;
        _mainNavigationView = mainNavigationView;
        _pageHost = pageHost;

        _pageHost.Loaded += OnPageHostLoaded;
        if (_pageHost.IsLoaded) OnPageHostLoaded(_pageHost, null!);
    }

    private void OnPageHostLoaded(object sender, RoutedEventArgs e)
    {
        var rootGrid = _rootGrid;
        var mainNavigationView = _mainNavigationView;
        var pageHost = _pageHost;
        if (_disposed || rootGrid is null || mainNavigationView is null || pageHost is null)
        {
            return;
        }

        _dropHost.Register(rootGrid);
        _dropHost.Register(mainNavigationView);
        _dropHost.Register(pageHost);
        _dropHost.SetHandlers(
            dragOver: e => _dragDropService.Value.HandleStandardDragOver(e, rootGrid),
            dropAsync: e => _dragDropService.Value.HandleStandardDropAsync(e, rootGrid));

        System.Diagnostics.Debug.WriteLine(
            "[MainWindow] DropHost connected to DragDropService standard handlers");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_pageHost is not null)
        {
            _pageHost.Loaded -= OnPageHostLoaded;
        }

        try
        {
            if (_dragDropService.IsValueCreated) _dragDropService.Value.PrepareForWindowClose();
        }
        finally
        {
            _dropHost.SetHandlers(null, null);
            _dropHost.UnregisterAll();

            _pageHost = null;
            _rootGrid = null;
            _mainNavigationView = null;
        }
    }
}
