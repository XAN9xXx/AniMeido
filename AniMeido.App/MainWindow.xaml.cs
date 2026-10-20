using AniMeido.App.Services;
using AniMeido.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.App
{
    public sealed partial class MainWindow : Window
    {
        private const string DefaultPageTypeName =
            "AniMeido.Plugin.Base.Views.TodayPage";

        private readonly PluginContributionRegistry _pluginContributions;
        private readonly NavigationService _navigationService;
        private readonly SplashCoordinator _splash;
        private readonly StartupDialogCoordinator _dialogs;
        private readonly MainWindowDragDropAdapter _dragDropAdapter;
        private readonly StartupMigrationGate? _migrationGate;
        private readonly CancellationTokenSource _migrationCancellation = new();
        private readonly string? _migrationBackupDirectory;
        private bool _initialNavigationStarted;
        internal TaskCompletionSource DataReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private NavigationViewItem? _lastSelectedPageItem;
        private bool _isClosing;
        private LegacyInstallationService? _legacyInstallationService;
        private IReadOnlyList<LegacyInstallation> _legacyInstallations = [];

        internal void ShowLegacyInstallationNotice(LegacyInstallationService service)
        {
            _legacyInstallationService = service;
            _legacyInstallations = service.Detect();
            if (_legacyInstallations.Count == 0)
                return;
            LegacyInstallationInfoBar.Visibility = Visibility.Visible;
            LegacyInstallationInfoBar.IsOpen = true;
        }

        private void OnLegacyInstallationInfoBarClosed(InfoBar sender, InfoBarClosedEventArgs args)
        {
            // 不写设置；本次窗口不再重新提示，下次启动重新检测。
            sender.Visibility = Visibility.Collapsed;
        }

        private async void OnUninstallLegacyClick(object sender, RoutedEventArgs args)
        {
            if (_isClosing || _legacyInstallationService is null)
                return;
            UninstallLegacyButton.IsEnabled = false;
            try
            {
                foreach (var installation in _legacyInstallations)
                {
                    if (!await _legacyInstallationService.UninstallAsync(installation))
                    {
                        if (!_isClosing)
                            LegacyInstallationInfoBar.Message = "旧版卸载未完成或已取消。请以后在 Windows“已安装的应用”中手动卸载；新版本可以继续使用。";
                        return;
                    }
                    if (_isClosing)
                        return;
                }
                _legacyInstallations = _legacyInstallationService.Detect();
                if (_legacyInstallations.Count == 0)
                    LegacyInstallationInfoBar.IsOpen = false;
                else
                    LegacyInstallationInfoBar.Message = "仍检测到旧版卸载登记。请在 Windows“已安装的应用”中确认旧版已经卸载。";
            }
#pragma warning disable CA1031 // async void UI 入口失败不能影响主窗口，也不能静默吞掉。
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "旧版 AniMeido 卸载提示操作失败");
                if (!_isClosing)
                    LegacyInstallationInfoBar.Message = "无法卸载旧版，请以后在 Windows“已安装的应用”中手动卸载。";
            }
#pragma warning restore CA1031
            finally
            {
                if (!_isClosing)
                    UninstallLegacyButton.IsEnabled = true;
            }
        }


        internal MainWindow(
            PluginContributionRegistry pluginContributions,
            NavigationService navigationService,
            MainWindowDragDropAdapter dragDropAdapter,
            StartupMigrationGate? migrationGate = null,
            string? migrationBackupDirectory = null)
        {
            InitializeComponent();
            if (AppDataRoots.IsIsolated)
            {
                Title += AppDataRoots.DisplaySuffix;
                AppTitleText.Text = Title;
                AppTitleText.Visibility = Visibility.Visible;
            }
            _migrationGate = migrationGate;
            _migrationBackupDirectory = migrationBackupDirectory;

            _navigationService = navigationService;
            _dragDropAdapter = dragDropAdapter
                ?? throw new ArgumentNullException(nameof(dragDropAdapter));
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
            TitleBarHelper.UseTallTitleBar(this);
            SetTitleBar(AppTitleBar);
            UpdateBackButton();

            App.ThemeService.ThemeChanged += (_, _) => UpdateTitleBarButtons();

            // 主窗口关闭时停止排队中的导航状态恢复
            Closed += OnMainWindowClosing;
            Closed += OnMainWindowClosed;

            // 适配器拥有 Loaded 订阅，窗口关闭时由 BeginShutdown/Closed 成对解除。
            if (_migrationGate is null) _dragDropAdapter.Attach(RootGrid, MainNaviView, ContentFrame);
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
            => await LoadMainAfterMigrationAsync();

        private async void OnRetryMigrationClick(object sender, RoutedEventArgs e)
            => await LoadMainAfterMigrationAsync();

        private async void OnOpenMigrationBackupsClick(object sender, RoutedEventArgs e)
        {
            if (_migrationBackupDirectory is null) return;
            try { await Windows.System.Launcher.LaunchFolderPathAsync(_migrationBackupDirectory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            { Serilog.Log.Warning(ex, "无法打开迁移备份文件夹"); }
        }

        private async Task LoadMainAfterMigrationAsync()
        {
            if (_initialNavigationStarted || _isClosing) return;
            _initialNavigationStarted = true;
            if (_migrationGate is { IsReady: false })
            {
                MainNaviView.Visibility = Visibility.Collapsed;
                MigrationBlock.Visibility = Visibility.Visible;
                SplashOverlay.Visibility = Visibility.Collapsed;
                MigrationRetry.IsEnabled = false;
                MigrationStatus.Text = "正在准备并迁移资料，请稍候…";
                var ready = await _migrationGate.TryRunAsync(_migrationCancellation.Token);
                if (_isClosing) return;
                MigrationRetry.IsEnabled = true;
                if (!ready)
                {
                    MigrationStatus.Text = "个人记录尚未完成迁移。请联网后重试；原有记录保留，完成前不会加载资料页面。";
                    _initialNavigationStarted = false;
                    return;
                }
                MigrationBlock.Visibility = Visibility.Collapsed;
                MainNaviView.Visibility = Visibility.Visible;
                _dragDropAdapter.Attach(RootGrid, MainNaviView, ContentFrame);
            }
            DataReady.TrySetResult();
            await _splash.WaitForImageAsync();
            if (_isClosing)
                return;

            var splashStart = DateTime.UtcNow;

            // 默认进入“今天”；目标页缺失时回退到第一个可用页面。
            var navigationItems = _pluginContributions.NavigationItems;
            if (MainNaviView.MenuItems.Count > 0 && navigationItems.Count > 0)
            {
                var initialItem = navigationItems.FirstOrDefault(item =>
                    item.Kind == PluginNavigationItemKind.Page
                    && item.PageType is not null
                    && string.Equals(
                        item.PageTypeName,
                        DefaultPageTypeName,
                        StringComparison.Ordinal))
                    ?? navigationItems.FirstOrDefault(item =>
                        item.Kind == PluginNavigationItemKind.Page
                        && item.PageType is not null);
                var initialMenuItem = MainNaviView.MenuItems
                    .OfType<NavigationViewItem>()
                    .FirstOrDefault(item => Equals(item.Tag, initialItem));
                if (initialItem?.PageType is not null && initialMenuItem is not null)
                {
                    MainNaviView.SelectedItem = initialMenuItem;
                    _lastSelectedPageItem = initialMenuItem;
                    _navigationService.NavigateTopLevel(initialItem.PageType);
                }
            }

            // 等待首次导航挂载页面（最多 10 秒），不等待页面的异步数据加载。
            await Task.WhenAny(_navigationService.FirstNavigationCompleted.Task, Task.Delay(10000));
            if (_isClosing)
                return;

            // 确保最低显示时间
            var elapsed = (DateTime.UtcNow - splashStart).TotalMilliseconds;
            if (elapsed < 2000)
                await Task.Delay((int)(2000 - elapsed));
            if (_isClosing)
                return;

            await _splash.FadeOutAsync();
            if (_isClosing)
                return;

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
            _migrationCancellation.Cancel();
            DataReady.TrySetCanceled();
            MainNaviView.Loaded -= OnMainNaviViewLoaded;
            MainNaviView.ItemInvoked -= OnNaviItemInvoked;
            _navigationService.Navigated -= OnNavigationServiceNavigated;
            _dragDropAdapter.Dispose();

            // 在窗口仍属于 UI 线程且 XamlRoot 有效时卸载当前页面。
            // 这样页面的 Unloaded 清理和异步取消不会延迟到 Window.Close 之后。
            ContentFrame.Content = null;
        }

        private void OnMainWindowClosing(object sender, WindowEventArgs args)
        {
            _isClosing = true;
            _migrationCancellation.Cancel();
            DataReady.TrySetCanceled();
        }

        private void OnMainWindowClosed(object sender, WindowEventArgs args)
        {
            _migrationCancellation.Dispose();
            _dragDropAdapter.Dispose();
            _pluginContributions.Changed -= OnPluginContributionsChanged;
        }

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

        private void OnNaviItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (_migrationGate is { IsReady: false }) return;
            var container = args.InvokedItemContainer as NavigationViewItem;

            if (container?.Tag is string tag && tag == "Settings")
            {
                if (_navigationService.CurrentPageType != typeof(Views.SettingPage))
                    _navigationService.NavigateTopLevel(typeof(Views.SettingPage));
                _lastSelectedPageItem = container;
                return;
            }
            if (container == null)
                return;

            if (_isClosing)
                return;

            if (container.Tag is PluginNavigationItem navItem)
            {
                if (navItem.Kind == PluginNavigationItemKind.Command && navItem.Command != null)
                {
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

                if (navItem.PageType != null)
                {
                    _navigationService.NavigateTopLevel(navItem.PageType);
                    _lastSelectedPageItem = container;
                }
            }
        }


    }
}
