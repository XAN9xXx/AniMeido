using AniMeido.App.Services;
using AniMeido.Contracts;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System.Diagnostics;
using Windows.UI;
using WinRT.Interop;

namespace AniMeido.App
{
    public sealed partial class MainWindow : Window
    {
        private readonly IReadOnlyList<PluginNavigationItem> _naviItems;



        public MainWindow(IReadOnlyList<PluginNavigationItem> naviItems)
        {
            InitializeComponent();

            // 非打包模式下用本地路径加载开屏图
            var splashPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "SplashScreen.png");
            if (System.IO.File.Exists(splashPath))
            {
                var img = new BitmapImage();
                img.UriSource = new Uri($"file:///{splashPath.Replace('\\', '/')}");
                SplashImage.Source = img;
            }

            // 设置 Alt+Tab 窗口图标
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
            if (System.IO.File.Exists(iconPath))
            {
                var hWnd = WindowNative.GetWindowHandle(this);
                var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
                var appWindow = AppWindow.GetFromWindowId(windowId);
                appWindow.SetIcon(iconPath);
            }

            _naviItems = naviItems;
            BuildNavigationMenu();

            MainNaviView.ItemInvoked += OnNaviItemInvoked;
            ContentFrame.Navigated += OnContentFrameNavigated;
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            UpdateBackButton();
        }

        private void UpdateTitleBarButtons()
        {
            var appWindow = GetAppWindow();
            var titleBar = appWindow.TitleBar;
            var theme = App.ThemeService.GetCurrentTheme();
            var isLightTheme = theme == ElementTheme.Light ||
                (theme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Light);

            titleBar.ButtonForegroundColor = isLightTheme ? Colors.Black : Colors.White;
            titleBar.ButtonHoverForegroundColor = isLightTheme ? Colors.Black : Colors.White;
            titleBar.ButtonPressedForegroundColor = isLightTheme ? Colors.Gray : Colors.Gray;
            titleBar.ButtonHoverBackgroundColor = isLightTheme
                ? Color.FromArgb(0x20, 0, 0, 0)
                : Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonPressedBackgroundColor = isLightTheme
                ? Color.FromArgb(0x30, 0, 0, 0)
                : Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF);
        }

        private AppWindow GetAppWindow()
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            return AppWindow.GetFromWindowId(windowId);
        }

        private void OnContentFrameNavigated(object sender, NavigationEventArgs e)
        {
            UpdateBackButton();
            SyncNavigationSelection(e.Content?.GetType());
        }

        private void SyncNavigationSelection(Type? pageType)
        {
            // Settings 页不匹配任何导航项 → 取消选中
            if (pageType == typeof(Views.SettingPage))
            {
                MainNaviView.SelectedItem = null;
                return;
            }

            var pageTypeName = pageType?.FullName;
            if (pageTypeName == null) return;

            // 遍历菜单项，找到匹配的
            foreach (var item in MainNaviView.MenuItems)
            {
                if (item is NavigationViewItem navItem &&
                    navItem.Tag is string tag &&
                    tag == pageTypeName)
                {
                    MainNaviView.SelectedItem = navItem;
                    return;
                }
            }
        }

        private void UpdateBackButton()
        {
            BackButton.IsEnabled = ContentFrame.CanGoBack;
            BackButton.Opacity = ContentFrame.CanGoBack ? 1.0 : 0.4;
        }

        private void OnBackButtonClick(object sender, RoutedEventArgs e)
        {
            if (ContentFrame.CanGoBack)
                ContentFrame.GoBack();
        }

        private async Task ShowPrivacyDialogAsync()
        {
            if (App.PrivacyService.IsAccepted()) return;

            var dialog = new ContentDialog
            {
                Title = "隐私声明",
                Content = "AniMeido 仅向 Bangumi API 请求番剧数据，不收集或发送任何个人信息。后续某些插件的功能可能涉及隐私问题，此类插件安装后，将在初次启动时以弹窗提醒。",
                PrimaryButtonText = "同意",
                CloseButtonText = "拒绝",
                XamlRoot = MainNaviView.XamlRoot
            };

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
                App.PrivacyService.Accept();
            else
                Environment.Exit(0);
        }

        private async Task ShowAnnouncementDialogAsync()
        {
            const string regKey = @"HKEY_CURRENT_USER\Software\AniMeido";
            var shown = Microsoft.Win32.Registry.GetValue(regKey, "AnnouncementShown", null);
            if (shown is int i && i == 1) return;

            var dialog = new ContentDialog
            {
                Title = "开发公告",
                Content = "AniMeido 正在持续开发中，功能会逐步完善。\n\n"
                    + "官方网站：animeido.com\n\n"
                    + "如果您有新想法或需要报告 Bug，请通过设置页的「关于」卡片中提供的联系方式与我们取得联系。",
                PrimaryButtonText = "知道了",
                XamlRoot = MainNaviView.XamlRoot
            };

            await dialog.ShowAsync();
            Microsoft.Win32.Registry.SetValue(regKey, "AnnouncementShown", 1);
        }

        private async void OnMainNaviViewLoaded(object sender, RoutedEventArgs e)
        {
            // 等待开屏图加载完成
            if (SplashImage.Source is BitmapImage bmp)
            {
                var tcs = new TaskCompletionSource();
                bmp.ImageOpened += (s, e) => tcs.TrySetResult();
                bmp.ImageFailed += (s, e) => tcs.TrySetResult();
                await Task.WhenAny(tcs.Task, Task.Delay(5000));
            }

            // 开屏至少显示 2 秒
            var splashStart = DateTime.UtcNow;

            // 开始导航到首页
            if (MainNaviView.MenuItems.Count > 0 && _naviItems.Count > 0)
            {
                MainNaviView.SelectedItem = MainNaviView.MenuItems[0];
                var firstItem = _naviItems[0];
                NavigateToPage(firstItem.PageType ?? Type.GetType(firstItem.PageTypeName));
            }

            // 等待首个页面数据加载完成
            await AppServices.FirstPageLoaded.Task;

            // 确保最低显示时间
            var elapsed = (DateTime.UtcNow - splashStart).TotalMilliseconds;
            if (elapsed < 2000)
                await Task.Delay((int)(2000 - elapsed));

            // 开屏淡出
            await FadeOutSplashAsync();

            // 触发当季页自动跳转到今日分组
            if (ContentFrame.Content is AniMeido.Plugin.Base.Views.CurrentSeasonPage seasonPage)
                seasonPage.TriggerAutoScroll();

            // 弹窗在开屏结束后显示
            await ShowPrivacyDialogAsync();
            await ShowAnnouncementDialogAsync();
            UpdateTitleBarButtons();
        }

        private async Task FadeOutSplashAsync()
        {
            await Task.Delay(800); // 让开屏图停留

            var visual = ElementCompositionPreview.GetElementVisual(SplashOverlay);
            var compositor = visual.Compositor;

            var fadeOut = compositor.CreateScalarKeyFrameAnimation();
            fadeOut.InsertKeyFrame(0.0f, 1.0f);
            fadeOut.InsertKeyFrame(1.0f, 0.0f);
            fadeOut.Duration = TimeSpan.FromMilliseconds(600);

            visual.StartAnimation("Opacity", fadeOut);

            await Task.Delay(800);
            SplashOverlay.Visibility = Visibility.Collapsed;
        }

        // 
        private void BuildNavigationMenu()
        {
            foreach (var item in _naviItems.Where(n => !n.IsSettingsPage))
            {
                var naviItem = new NavigationViewItem
                {
                    Content = item.Label,
                    Icon = new FontIcon { Glyph = item.Icon, FontSize = 18 },
                    Tag = item.PageTypeName,
                    Margin = new Thickness(0, 6, 0, 6),
                    MinHeight = 56,
                };
                MainNaviView.MenuItems.Add(naviItem);
            }

            // 手动添加设置按钮
            var settingsItem = new NavigationViewItem
            {
                Content = "设置",
                Icon = new FontIcon { Glyph = "\uE713", FontSize = 18 },
                Tag = "Settings",
                Margin = new Thickness(0, 6, 0, 6),
                MinHeight = 56,
            };
            MainNaviView.FooterMenuItems.Add(settingsItem);

            MainNaviView.Loaded += OnMainNaviViewLoaded;
        }

        // 
        private void OnNaviItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            var container = args.InvokedItemContainer as NavigationViewItem;

            // 设置页走单独的导航路径
            if (container?.Content?.ToString() == "设置")
            {
                if (ContentFrame.Content?.GetType() != typeof(Views.SettingPage))
                    ContentFrame.Navigate(typeof(Views.SettingPage));
                return;
            }
            if (container == null)
                return;

            var pageTypeName = container.Tag as string;
            if (string.IsNullOrEmpty(pageTypeName))
                return;

            // 通过 PageTypeName 查找对应的导航项，获取 Type
            var navItem = _naviItems.FirstOrDefault(n => n.PageTypeName == pageTypeName);
            var pageType = navItem?.PageType ?? System.Type.GetType(pageTypeName);
            if (pageType != null)
                NavigateToPage(pageType);
        }

        //
        private void NavigateToPage(Type? pageType)
        {
            if (pageType == null) return;
            if (ContentFrame.Content?.GetType() == pageType) return;

            var pageFactory = App.Services?.GetService(typeof(PageFactory)) as PageFactory;
            if (pageFactory != null)
            {
                var page = pageFactory.CreatePage(pageType);
                ContentFrame.Content = page;
                SyncNavigationSelection(pageType);
            }
            else
            {
                ContentFrame.Navigate(pageType, null, new EntranceNavigationTransitionInfo());
            }
        }
    }
}
