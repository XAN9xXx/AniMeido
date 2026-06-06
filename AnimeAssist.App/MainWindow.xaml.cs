using AniMeido.App.Services;
using AniMeido.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace AniMeido.App
{
    public sealed partial class MainWindow : Window
    {
        private readonly IReadOnlyList<PluginNavigationItem> _naviItems;
        private readonly NavigationService _navigationService;
        private readonly SplashCoordinator _splash;
        private readonly StartupDialogCoordinator _dialogs;



        public MainWindow(IReadOnlyList<PluginNavigationItem> naviItems, NavigationService navigationService)
        {
            InitializeComponent();

            _navigationService = navigationService;
            _navigationService.Initialize(ContentFrame);
            _navigationService.Navigated += OnNavigationServiceNavigated;

            _splash = new SplashCoordinator(SplashImage, SplashOverlay);
            _splash.LoadSplashImage();
            _dialogs = new StartupDialogCoordinator(MainNaviView);

            TitleBarHelper.SetWindowIcon(this);

            _naviItems = naviItems;
            BuildNavigationMenu();

            MainNaviView.ItemInvoked += OnNaviItemInvoked;
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            UpdateBackButton();

            // 主题切换时同步标题栏按钮颜色
            App.ThemeService.ThemeChanged += (_, _) => UpdateTitleBarButtons();
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

            var splashStart = DateTime.UtcNow;

            // 开始导航到首页
            if (MainNaviView.MenuItems.Count > 0 && _naviItems.Count > 0)
            {
                MainNaviView.SelectedItem = MainNaviView.MenuItems[0];
                var firstItem = _naviItems[0];
                if (firstItem.PageType != null)
                    _navigationService.NavigateTopLevel(firstItem.PageType);
            }

            // 等待首个页面加载完成（超时 10 秒兜底）
            await Task.WhenAny(_navigationService.FirstNavigationCompleted.Task, Task.Delay(10000));

            // 确保最低显示时间
            var elapsed = (DateTime.UtcNow - splashStart).TotalMilliseconds;
            if (elapsed < 2000)
                await Task.Delay((int)(2000 - elapsed));

            // 开屏淡出
            await _splash.FadeOutAsync();

            // 弹窗在开屏结束后显示
            await _dialogs.ShowPrivacyDialogAsync();
            await _dialogs.ShowAnnouncementDialogAsync();
            UpdateTitleBarButtons();
        }

        // 
        private void BuildNavigationMenu()
        {
            NavigationMenuBuilder.Build(MainNaviView, _naviItems);
            MainNaviView.Loaded += OnMainNaviViewLoaded;
        }

        // 
        private void OnNaviItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            var container = args.InvokedItemContainer as NavigationViewItem;

            // 设置页走单独的导航路径（顶层导航，清空返回栈）
            if (container?.Tag is string tag && tag == "Settings")
            {
                if (_navigationService.CurrentPageType != typeof(Views.SettingPage))
                    _navigationService.NavigateTopLevel(typeof(Views.SettingPage));
                return;
            }
            if (container == null)
                return;

            // 主导航项点击使用 NavigateTopLevel，清空返回栈
            if (container.Tag is PluginNavigationItem navItem && navItem.PageType != null)
                _navigationService.NavigateTopLevel(navItem.PageType);
        }


    }
}
