using AniMeido.Plugin.Base.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.App.Services;

/// <summary>
/// App 层主窗口拖放接线适配器。
/// Base 的拖放服务负责业务；此类型拥有主窗口元素的事件订阅和宿主注册。
/// </summary>
internal sealed class MainWindowDragDropAdapter : IDisposable
{
    private readonly DragDropService _dragDropService;
    private readonly AnimeCardDropHost _dropHost = new();
    private Frame? _contentFrame;
    private UIElement? _rootGrid;
    private UIElement? _mainNavigationView;
    private bool _disposed;

    public MainWindowDragDropAdapter(DragDropService dragDropService)
    {
        _dragDropService = dragDropService
            ?? throw new ArgumentNullException(nameof(dragDropService));
    }

    public void Attach(
        UIElement rootGrid,
        UIElement mainNavigationView,
        Frame contentFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_contentFrame is not null)
        {
            throw new InvalidOperationException("主窗口拖放适配器只能绑定一次。");
        }

        ArgumentNullException.ThrowIfNull(rootGrid);
        ArgumentNullException.ThrowIfNull(mainNavigationView);
        ArgumentNullException.ThrowIfNull(contentFrame);

        _rootGrid = rootGrid;
        _mainNavigationView = mainNavigationView;
        _contentFrame = contentFrame;

        _contentFrame.Loaded += OnContentFrameLoaded;
    }

    private void OnContentFrameLoaded(object sender, RoutedEventArgs e)
    {
        var rootGrid = _rootGrid;
        var mainNavigationView = _mainNavigationView;
        var contentFrame = _contentFrame;
        if (_disposed || rootGrid is null || mainNavigationView is null || contentFrame is null)
        {
            return;
        }

        _dropHost.Register(rootGrid);
        _dropHost.Register(mainNavigationView);
        _dropHost.Register(contentFrame);
        _dropHost.SetHandlers(
            dragOver: e => _dragDropService.HandleStandardDragOver(e, rootGrid),
            dropAsync: e => _dragDropService.HandleStandardDropAsync(e, rootGrid));

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

        if (_contentFrame is not null)
        {
            _contentFrame.Loaded -= OnContentFrameLoaded;
        }

        try
        {
            _dragDropService.PrepareForWindowClose();
        }
        finally
        {
            _dropHost.SetHandlers(null, null);
            _dropHost.UnregisterAll();

            _contentFrame = null;
            _rootGrid = null;
            _mainNavigationView = null;
        }
    }
}
