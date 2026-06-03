using AniMeido.App.Helpers;
using AniMeido.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.App.Services
{
    /// <summary>
    /// App 层导航服务：负责页面创建、返回栈维护、导航参数传递。
    /// 实现 IPluginNavigator 供插件层请求导航。
    ///
    /// 返回栈保存页面实例 + 导航参数，确保返回时完整恢复页面内部状态（搜索关键词、滚动位置等）。
    /// 顶层导航（主导航、设置页）会清空返回栈。
    /// </summary>
    public sealed class NavigationService : IPluginNavigator
    {
        private readonly PageFactory _pageFactory;
        private readonly ILogger<NavigationService> _logger;
        private readonly NavigationStack _backStack = new();
        private Frame? _frame;
        private object? _lastParameter;

        /// <summary>是否有上一页可返回。</summary>
        public bool CanGoBack => _backStack.CanGoBack;

        /// <summary>当前页面类型。</summary>
        public Type? CurrentPageType { get; private set; }

        /// <summary>是否已完成首次导航。</summary>
        public bool IsFirstNavigationCompleted { get; private set; }

        /// <summary>首次导航完成信号，用于替代 AppServices.FirstPageLoaded。</summary>
        internal TaskCompletionSource FirstNavigationCompleted { get; } = new();

        /// <summary>导航完成后触发，参数为目标页面类型。</summary>
        public event Action<Type?>? Navigated;

        public NavigationService(PageFactory pageFactory, ILogger<NavigationService> logger)
        {
            _pageFactory = pageFactory;
            _logger = logger;
        }

        /// <summary>
        /// 绑定宿主 Frame。
        /// </summary>
        public void Initialize(Frame frame)
        {
            _frame = frame;
        }

        /// <summary>
        /// 导航到指定页面（实现 IPluginNavigator）。
        /// 由插件内部发起的导航（详情页、Tag 页、人物页）压入返回栈。
        /// </summary>
        public void Navigate(Type pageType, object? parameter = null)
        {
            NavigateCore(pageType, parameter, NavigationMode.Push);
        }

        /// <summary>
        /// 顶层导航：切换主导航项目，清空返回栈。
        /// 由 MainWindow 点击主导航项时调用。
        /// </summary>
        public void NavigateTopLevel(Type pageType)
        {
            NavigateCore(pageType, parameter: null, NavigationMode.TopLevel);
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

            var entry = _backStack.Pop();

            _frame.Content = entry.Page;
            CurrentPageType = entry.Page.GetType();
            _lastParameter = entry.Parameter;
            Navigated?.Invoke(CurrentPageType);

            // 注意：不重新调用 OnNavigatedToAsync。
            // 返回栈保存的是完整的页面实例，内部状态（搜索词、滚动位置、数据列表等）应保持原样。
            // 重新触发会丢失这些状态，与"保存页面实例"的设计目标冲突。
        }

        /// <summary>清空返回栈。</summary>
        public void ClearBackStack() => _backStack.Clear();

        private enum NavigationMode { Push, TopLevel }

        private void NavigateCore(Type pageType, object? parameter, NavigationMode mode)
        {
            if (_frame == null)
                throw new InvalidOperationException("NavigationService 未初始化。请先调用 Initialize(Frame)。");

            // 相同页面 + 无参数时不重复导航
            if (mode != NavigationMode.TopLevel && CurrentPageType == pageType && parameter == null)
                return;

            // 顶层导航清空返回栈，否则保存当前页面实例到返回栈
            if (mode == NavigationMode.TopLevel)
            {
                _backStack.Clear();
            }
            else if (CurrentPageType != null && _frame.Content is Page currentPage)
            {
                // 返回栈顶不是当前页时才压入
                if (_backStack.Peek() is not { } top || (Page)top.Page != currentPage)
                    _backStack.Push(currentPage, _lastParameter);
            }

            _lastParameter = parameter;

            var page = _pageFactory.CreatePage(pageType);

            _frame.Content = page;
            CurrentPageType = pageType;
            Navigated?.Invoke(pageType);

            // 页面挂载到 Frame 后再触发异步通知；fire-and-forget，不阻塞导航
            if (page is INavigationAware aware)
                InvokeNavigationAwareAsync(aware, parameter).Forget(_logger, "InvokeNavigationAware");

            // 首次导航完成时设置信号
            if (!IsFirstNavigationCompleted)
            {
                IsFirstNavigationCompleted = true;
                FirstNavigationCompleted.TrySetResult();
            }
        }

        /// <summary>
        /// 安全调用 INavigationAware.OnNavigatedToAsync，捕获并记录异常。
        /// fire-and-forget，不阻塞导航。
        /// </summary>
        private async Task InvokeNavigationAwareAsync(INavigationAware aware, object? parameter)
        {
            try
            {
                await aware.OnNavigatedToAsync(parameter);
            }
#pragma warning disable CA1031 // 页面通知异常不应影响导航流程
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "OnNavigatedToAsync failed for page {PageType} with parameter {Parameter}",
                    aware.GetType().FullName, parameter);
#pragma warning restore CA1031
            }
        }
    }
}
