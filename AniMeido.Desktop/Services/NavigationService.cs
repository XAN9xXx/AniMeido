using AniMeido.App.Helpers;
using AniMeido.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.App.Services
{
    internal enum DesktopNavigationKind { TopLevel, Forward, Back }

    /// <summary>
    /// 返回栈保存页面实例 + 导航参数，确保返回时完整恢复页面内部状态（搜索关键词、滚动位置等）。
    /// 顶层导航（主导航、设置页）会清空返回栈。
    /// </summary>
    public sealed class NavigationService : IPluginNavigator
    {
        private readonly PageFactory _pageFactory;
        private readonly ILogger<NavigationService> _logger;
        private readonly NavigationStack _backStack = new();
        private Frame? _frame;
        private Func<Frame, Page, DesktopNavigationKind, Frame>? _mount;
        internal Frame? ActiveFrame => _frame;
        private object? _lastParameter;
        private bool _isStopped;

        internal DesktopNavigationKind LastNavigationKind { get; private set; }

        public bool CanGoBack => _backStack.CanGoBack;

        public Type? CurrentPageType { get; private set; }

        public bool IsFirstNavigationCompleted { get; private set; }

        /// <summary>首次导航挂载页面后的信号，不等待页面异步数据加载。</summary>
        internal TaskCompletionSource FirstNavigationCompleted { get; } = new();

        public event Action<Type?>? Navigated;

        public NavigationService(PageFactory pageFactory, ILogger<NavigationService> logger)
        {
            _pageFactory = pageFactory;
            _logger = logger;
        }

        public void Initialize(Frame frame)
            => Initialize(frame, null);

        internal void Initialize(Frame frame, Func<Frame, Page, DesktopNavigationKind, Frame>? mount)
        {
            ObjectDisposedException.ThrowIf(_isStopped, this);
            _frame = frame;
            _mount = mount;
        }

        private void Mount(Page page, DesktopNavigationKind kind)
        {
            if (_mount is null) _frame!.Content = page;
            else _frame = _mount(_frame!, page, kind);
        }

        /// <summary>在 UI 线程停止接纳导航并释放页面引用；控件卸载由窗口负责。</summary>
        internal void Stop()
        {
            _isStopped = true;
            _frame = null;
            _mount = null;
            _backStack.Clear();
            _lastParameter = null;
            CurrentPageType = null;
        }

        /// <summary>
        /// 由插件内部发起的导航（详情页、Tag 页、人物页）压入返回栈。
        /// </summary>
        public void Navigate(Type pageType, object? parameter = null)
        {
            NavigateCore(pageType, parameter, DesktopNavigationKind.Forward);
        }

        /// <summary>
        /// 顶层导航：切换主导航项目，清空返回栈。
        /// 由 MainWindow 点击主导航项时调用。
        /// </summary>
        public void NavigateTopLevel(Type pageType)
        {
            NavigateCore(pageType, parameter: null, DesktopNavigationKind.TopLevel);
        }

        /// <summary>
        /// 返回上一页，完整恢复页面实例及其内部状态。
        /// 不重新调用 OnNavigatedToAsync，因为页面实例已恢复，内部状态应保留。
        /// 如果未来需要返回刷新的语义，应使用单独的返回生命周期接口。
        /// </summary>
        public void GoBack()
        {
            if (_frame == null || !_backStack.CanGoBack)
                return;

            var entry = _backStack.Peek()!;

            Mount((Page)entry.Page, DesktopNavigationKind.Back);
            _backStack.Pop();
            CurrentPageType = entry.Page.GetType();
            _lastParameter = entry.Parameter;
            LastNavigationKind = DesktopNavigationKind.Back;
            Navigated?.Invoke(CurrentPageType);
        }

        public void ClearBackStack() => _backStack.Clear();

        private void NavigateCore(Type pageType, object? parameter, DesktopNavigationKind kind)
        {
            if (_isStopped)
                return;

            if (_frame == null)
                throw new InvalidOperationException("NavigationService 未初始化。请先调用 Initialize(Frame)。");

            // 同一页面目标的重复请求不创建第二个页面实例，否则返回时
            // 会先恢复一个视觉上完全相同的页面，看起来需要点击两次。
            if (kind != DesktopNavigationKind.TopLevel
                && IsSameTarget(
                    CurrentPageType,
                    _lastParameter,
                    pageType,
                    parameter))
            {
                return;
            }

            var previousPage = _frame.Content;
            Page page;
            try
            {
                page = _pageFactory.CreatePage(pageType);
                Mount(page, kind);
            }
            catch (Exception ex)
            {
                ex.Data["AniMeido.TargetPage"] = pageType.FullName;
                throw;
            }
            if (kind == DesktopNavigationKind.TopLevel)
            {
                _backStack.Clear();
            }
            else if (CurrentPageType != null && previousPage is Page currentPage)
            {
                if (_backStack.Peek() is not { } top || (Page)top.Page != currentPage)
                    _backStack.Push(currentPage, _lastParameter);
            }

            _lastParameter = parameter;

            CurrentPageType = pageType;
            LastNavigationKind = kind;
            Navigated?.Invoke(pageType);

            // 页面挂载到 Frame 后再触发异步通知；fire-and-forget，不阻塞导航
            if (page is INavigationAware aware)
                InvokeNavigationAwareAsync(aware, parameter).Forget(_logger, "InvokeNavigationAware");

            if (!IsFirstNavigationCompleted)
            {
                IsFirstNavigationCompleted = true;
                FirstNavigationCompleted.TrySetResult();
            }
        }

        internal static bool IsSameTarget(
            Type? currentPageType,
            object? currentParameter,
            Type targetPageType,
            object? targetParameter)
            => currentPageType == targetPageType
                && Equals(currentParameter, targetParameter);

        private async Task InvokeNavigationAwareAsync(INavigationAware aware, object? parameter)
        {
            try
            {
                await aware.OnNavigatedToAsync(parameter);
            }
#pragma warning disable CA1031 // 页面通知异常不应影响导航流程
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "OnNavigatedToAsync failed for page {PageType}",
                    aware.GetType().FullName);
#pragma warning restore CA1031
            }
        }
    }
}
