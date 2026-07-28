using AniMeido.App.Services;
using AniMeido.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace AniMeido.App
{
    public sealed partial class MainWindow : Window
    {
        private readonly PluginContributionRegistry _pluginContributions;
        private readonly NavigationService _navigationService;
        private readonly SplashCoordinator _splash;
        private readonly StartupDialogCoordinator _dialogs;
        private readonly AnimeCardDropHost _dropHost = new();

        private NavigationViewItem? _lastSelectedPageItem;
        private bool _isClosing;


        public MainWindow(
            PluginContributionRegistry pluginContributions,
            NavigationService navigationService)
        {
            InitializeComponent();

            _navigationService = navigationService;
            _navigationService.Initialize(ContentFrame);
            _navigationService.Navigated += OnNavigationServiceNavigated;

            _splash = new SplashCoordinator(SplashImage, SplashOverlay);
            _splash.LoadSplashImage();
            _dialogs = new StartupDialogCoordinator(MainNaviView);

            TitleBarHelper.SetWindowIcon(this);

            _pluginContributions = pluginContributions;
            _pluginContributions.Changed += OnPluginContributionsChanged;
            BuildNavigationMenu();

            MainNaviView.ItemInvoked += OnNaviItemInvoked;
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            UpdateBackButton();

            // 主题切换时同步标题栏按钮颜色
            App.ThemeService.ThemeChanged += (_, _) => UpdateTitleBarButtons();

            // 主窗口关闭时停止排队中的导航状态恢复
            Closed += OnMainWindowClosing;
            Closed += (_, _) =>
                _pluginContributions.Changed -= OnPluginContributionsChanged;

            // 主窗口关闭时注销全部 DropHost
            Closed += (_, _) => _dropHost.UnregisterAll();

            // 布局完成后注册 AnimeCard 拖放兜底到多个宿主元素
            // 使用 AddHandler(handledEventsToo=true) 确保不被子控件拦截 DragOver
            ContentFrame.Loaded += (_, _) =>
            {
                // 注册三层宿主：RootGrid（全窗口）→ MainNaviView（导航区）→ ContentFrame（页面区）
                _dropHost.Register(RootGrid);
                _dropHost.Register(MainNaviView);
                _dropHost.Register(ContentFrame);

                // 连接 DragDropService 标准拖放处理器
                var ddService = App.Services?.GetService(typeof(AniMeido.Plugin.Base.Services.DragDropService))
                    as AniMeido.Plugin.Base.Services.DragDropService;
                if (ddService != null)
                {
                    _dropHost.SetHandlers(
                        dragOver: (e) => ddService.HandleStandardDragOver(e, RootGrid),
                        dropAsync: async (e) => await ddService.HandleStandardDropAsync(e, RootGrid)
                    );
                    System.Diagnostics.Debug.WriteLine("[MainWindow] DropHost connected to DragDropService standard handlers");
                }
            };
        }

        private void UpdateTitleBarButtons()
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            TitleBarHelper.UpdateButtonColors(Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId));
        }

        private void OnNavigationServiceNavigated(Type? pageType)
        {
            UpdateBackButton();
            SyncNavigationSelection(pageType);
        }

        private void SyncNavigationSelection(Type? pageType)
        {
            if (pageType == null) return;

            // Settings 页：从 FooterMenuItems 中找到 "设置" 项
            if (pageType == typeof(Views.SettingPage))
            {
                foreach (var item in MainNaviView.FooterMenuItems)
                {
                    if (item is NavigationViewItem navItem &&
                        navItem.Tag is string tag &&
                        tag == "Settings")
                    {
                        MainNaviView.SelectedItem = navItem;
                        return;
                    }
                }

                MainNaviView.SelectedItem = null;
                return;
            }

            // 普通页面：按 PluginNavigationItem.PageType 匹配
            foreach (var item in MainNaviView.MenuItems)
            {
                if (item is NavigationViewItem navItem &&
                    navItem.Tag is PluginNavigationItem pluginItem &&
                    pluginItem.PageType == pageType)
                {
                    MainNaviView.SelectedItem = navItem;
                    return;
                }
            }
        }

        private void UpdateBackButton()
        {
            BackButton.IsEnabled = _navigationService.CanGoBack;
            BackButton.Opacity = _navigationService.CanGoBack ? 1.0 : 0.4;
        }

        private void OnBackButtonClick(object sender, RoutedEventArgs e)
        {
            _navigationService.GoBack();
        }

        private async void OnMainNaviViewLoaded(object sender, RoutedEventArgs e)
        {
            await _splash.WaitForImageAsync();
            if (_isClosing)
                return;

            var splashStart = DateTime.UtcNow;

            // 开始导航到首页
            var navigationItems = _pluginContributions.NavigationItems;
            if (MainNaviView.MenuItems.Count > 0 && navigationItems.Count > 0)
            {
                var firstItem = navigationItems[0];
                if (firstItem.Kind == PluginNavigationItemKind.Page && firstItem.PageType != null)
                {
                    MainNaviView.SelectedItem = MainNaviView.MenuItems[0];
                    _lastSelectedPageItem = MainNaviView.MenuItems[0] as NavigationViewItem;
                    _navigationService.NavigateTopLevel(firstItem.PageType);
                }
            }

            // 等待首个页面加载完成（超时 10 秒兜底）
            await Task.WhenAny(_navigationService.FirstNavigationCompleted.Task, Task.Delay(10000));
            if (_isClosing)
                return;

            // 确保最低显示时间
            var elapsed = (DateTime.UtcNow - splashStart).TotalMilliseconds;
            if (elapsed < 2000)
                await Task.Delay((int)(2000 - elapsed));
            if (_isClosing)
                return;

            // 开屏淡出
            await _splash.FadeOutAsync();
            if (_isClosing)
                return;

            // 弹窗在开屏结束后显示
            await _dialogs.ShowPrivacyDialogAsync();
            if (_isClosing)
                return;
            await _dialogs.ShowAnnouncementDialogAsync();
            if (_isClosing)
                return;
            UpdateTitleBarButtons();
        }

        internal void BeginShutdown()
        {
            _isClosing = true;
            MainNaviView.Loaded -= OnMainNaviViewLoaded;
            MainNaviView.ItemInvoked -= OnNaviItemInvoked;
            _navigationService.Navigated -= OnNavigationServiceNavigated;
        }

        //
        private void OnMainWindowClosing(object sender, WindowEventArgs args)
        {
            _isClosing = true;
        }

        //
        private void BuildNavigationMenu()
        {
            NavigationMenuBuilder.Build(
                MainNaviView,
                _pluginContributions.NavigationItems);
            MainNaviView.Loaded += OnMainNaviViewLoaded;
        }

        private void OnPluginContributionsChanged(
            object? sender,
            EventArgs e)
            => DispatcherQueue.TryEnqueue(() =>
            {
                var selectedPage = _lastSelectedPageItem?.Tag;
                NavigationMenuBuilder.Build(
                    MainNaviView,
                    _pluginContributions.NavigationItems);
                _lastSelectedPageItem = MainNaviView.MenuItems
                    .OfType<NavigationViewItem>()
                    .FirstOrDefault(item => Equals(item.Tag, selectedPage));
                if (_lastSelectedPageItem is not null)
                {
                    MainNaviView.SelectedItem = _lastSelectedPageItem;
                }
            });

        // 
        private void OnNaviItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            var container = args.InvokedItemContainer as NavigationViewItem;

            // 设置页走单独的导航路径（顶层导航，清空返回栈）
            if (container?.Tag is string tag && tag == "Settings")
            {
                if (_navigationService.CurrentPageType != typeof(Views.SettingPage))
                    _navigationService.NavigateTopLevel(typeof(Views.SettingPage));
                _lastSelectedPageItem = container;
                return;
            }
            if (container == null)
                return;

            // 应用关闭阶段不再处理导航点击
            if (_isClosing)
                return;

            if (container.Tag is PluginNavigationItem navItem)
            {
                if (navItem.Kind == PluginNavigationItemKind.Command && navItem.Command != null)
                {
                    // Command 类型入口：执行命令，不改变主窗口导航状态
                    if (navItem.Command.CanExecute(null))
                        navItem.Command.Execute(null);

                    // 延迟恢复选中项，应对 NavigationView 内部状态覆盖
                    _ = DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_isClosing) return;
                        MainNaviView.SelectedItem = _lastSelectedPageItem;
                    });
                    return;
                }

                // Page 类型入口：原有导航逻辑
                if (navItem.PageType != null)
                {
                    _navigationService.NavigateTopLevel(navItem.PageType);
                    _lastSelectedPageItem = container;
                }
            }
        }


    }
}
