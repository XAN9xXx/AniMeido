using AniMeido.App.Services;
using AniMeido.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace AniMeido.App
{

    public partial class App : Application
    {
        private MainWindow? _window;
        private bool _isUiErrorDialogShown;
        private bool _shutdownStarted;
        private bool _shutdownCompleted;
        private bool _exitRequested;
        private DesktopSettings _desktopSettings = new();
        private ServiceProvider? _serviceProvider;
        private AppWindow? _mainAppWindow;
        private AppWindowActivationService? _activationService;

        public App()
        {
            StartupLogger.Initialize();
            GlobalExceptionHandler.Register();
            UnhandledException += OnAppUnhandledException;
            InitializeComponent();
        }

        public static IServiceProvider? Services { get; private set; }
        public static Window? MainWindow { get; private set; }
        public static ThemeService ThemeService { get; } = new ThemeService();
        public static PrivacyService PrivacyService { get; } = new PrivacyService();
        public static IReadOnlyList<IPlugin>? Plugins { get; private set; }
        public static string? LatestVersion { get; internal set; }

        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            try
            {
                await InitializeApplicationAsync();
            }
#pragma warning disable CA1031 // 启动失败无法继续，显示独立于 WinUI 的错误对话框。
            catch (Exception ex)
            {
                GlobalExceptionHandler.ShowFatalError(
                    "AniMeido 启动失败，请查看错误日志。", IntPtr.Zero, ex);
                Exit();
            }
#pragma warning restore CA1031
        }

        private async Task InitializeApplicationAsync()
        {
            var services = new ServiceCollection()
                .AddAppServices();

            var pluginPackageManager = PluginPackageManager.CreateDefault();
            services.AddSingleton(pluginPackageManager);

            // 插件加载（在 ServiceProvider 构建前，因为 PluginHost 需要向 services 注册插件服务）
            var (navItems, plugins) = await PluginStartup.LoadPluginsAsync(services);
            App.Plugins = plugins;

            // 构建 DI 容器并初始化数据库
            var provider = services.BuildServiceProvider();
            _serviceProvider = provider;
            Services = provider;
            var notificationInitialization = provider
                .GetRequiredService<WindowsAppNotificationService>()
                .InitializeAsync();
            var databaseInitialization = DatabaseStartup.InitializeAsync(provider);
            var desktopSettingsLoad = provider
                .GetRequiredService<DesktopSettingsStore>()
                .LoadAsync();
            await Task.WhenAll(
                notificationInitialization,
                databaseInitialization,
                desktopSettingsLoad);
            _desktopSettings = await desktopSettingsLoad;

            // 创建主窗口
            var contributionRegistry =
                provider.GetRequiredService<PluginContributionRegistry>();
            contributionRegistry.SetBuiltInItems(navItems);
            _window = new MainWindow(
                contributionRegistry,
                provider.GetRequiredService<NavigationService>(),
                new MainWindowDragDropAdapter(
                    provider.GetRequiredService<
                        AniMeido.Plugin.Base.Services.DragDropService>()));
            MainWindow = _window;

            var pluginHostSupervisor =
                provider.GetRequiredService<PluginHostSupervisor>();
            var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(
                _window);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(
                windowHandle);
            _mainAppWindow = AppWindow.GetFromWindowId(windowId);
            _activationService =
                provider.GetRequiredService<AppWindowActivationService>();
            _activationService.Attach(_window, _mainAppWindow);
            _mainAppWindow.Closing += OnMainAppWindowClosing;
            _window.Closed += (_, _) =>
            {
                MainWindow = null;
                _activationService?.Detach();
                _activationService = null;
                _mainAppWindow = null;
            };
            _window.Activate();
            await provider.GetRequiredService<GlobalShortcutManager>()
                .StartAsync();
            if (_desktopSettings.KeepInTrayOnClose)
            {
                await StartTrayIconAtStartupAsync(provider);
            }

            if (_window.Content is FrameworkElement root)
                ThemeService.InitializeTheme(root);

            _ = StartPluginHostSafelyAsync(pluginHostSupervisor);
            _ = UpdateStartupTask.CheckForUpdateSilentlyAsync(provider, _window);
        }

        private async void OnMainAppWindowClosing(
            AppWindow sender,
            AppWindowClosingEventArgs args)
        {
            if (_shutdownCompleted)
            {
                return;
            }

            args.Cancel = true;
            if (!_exitRequested
                && _desktopSettings.KeepInTrayOnClose)
            {
                _serviceProvider?
                    .GetRequiredService<AppWindowActivationService>()
                    .HideMainWindow();
                return;
            }

            if (_shutdownStarted)
            {
                return;
            }

            _shutdownStarted = true;
            _window?.BeginShutdown();
            try
            {
                var provider = _serviceProvider;
                _serviceProvider = null;
                if (provider is not null)
                {
                    await provider.DisposeAsync();
                }
            }
#pragma warning disable CA1031 // Shutdown cleanup failure must not trap the window open.
            catch (Exception ex)
            {
                Log.Warning(ex, "应用关闭清理失败");
            }
#pragma warning restore CA1031
            finally
            {
                Services = null;
                _shutdownCompleted = true;
                if (_mainAppWindow is not null)
                {
                    _mainAppWindow.Closing -= OnMainAppWindowClosing;
                    _mainAppWindow = null;
                }
                _activationService?.Detach();
                Log.CloseAndFlush();
                _window?.Close();
            }
        }

        internal async Task SetKeepInTrayOnCloseAsync(bool enabled)
        {
            var updatedSettings = _desktopSettings with
            {
                KeepInTrayOnClose = enabled,
            };
            var provider = _serviceProvider;
            if (provider is null)
            {
                return;
            }

            var trayIcon = provider.GetRequiredService<TrayIconService>();
            if (enabled)
            {
                StartTrayIcon(provider);
            }
            else
            {
                trayIcon.Stop();
            }

            try
            {
                await provider.GetRequiredService<DesktopSettingsStore>()
                    .SaveAsync(updatedSettings);
                _desktopSettings = updatedSettings;
            }
            catch
            {
                if (enabled)
                {
                    trayIcon.Stop();
                }
                else if (_desktopSettings.KeepInTrayOnClose)
                {
                    StartTrayIcon(provider);
                }

                throw;
            }
        }

        internal void RequestExit()
        {
            _exitRequested = true;
            _window?.Close();
        }

        private void StartTrayIcon(IServiceProvider provider)
            => provider.GetRequiredService<TrayIconService>().Start(
                () => provider
                    .GetRequiredService<AppWindowActivationService>()
                    .ActivateMainWindow(),
                RequestExit);

        private async Task StartTrayIconAtStartupAsync(
            IServiceProvider provider)
        {
            try
            {
                StartTrayIcon(provider);
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or TimeoutException)
            {
                Log.Warning(
                    ex,
                    "托盘图标启动失败，已关闭托盘驻留设置");
                _desktopSettings = _desktopSettings with
                {
                    KeepInTrayOnClose = false,
                };
                try
                {
                    await provider.GetRequiredService<DesktopSettingsStore>()
                        .SaveAsync(_desktopSettings);
                }
                catch (Exception saveException) when (
                    saveException is IOException
                    or UnauthorizedAccessException)
                {
                    Log.Warning(
                        saveException,
                        "无法保存托盘驻留降级设置");
                }
            }
        }

        private static async Task StartPluginHostSafelyAsync(
            PluginHostSupervisor supervisor)
        {
            try
            {
                await supervisor.StartAsync();
            }
#pragma warning disable CA1031 // Optional plugins must not prevent the core App from running.
            catch (Exception ex)
            {
                Log.Warning(ex, "PluginHost 启动失败，可选插件已跳过");
            }
#pragma warning restore CA1031
        }

        private void OnAppUnhandledException(object? sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            if (_window is null)
            {
                GlobalExceptionHandler.ShowFatalError(
                    "AniMeido 无法创建主窗口，请查看错误日志。", IntPtr.Zero, e.Exception);
                e.Handled = true;
                Exit();
                return;
            }

            Log.Warning(
                e.Exception,
                "[UI] WinUI 未处理异常，目标页面 {TargetPage}",
                e.Exception.Data["AniMeido.TargetPage"]);
            e.Handled = true;
            ShowUiErrorDialog();
        }

        private void ShowUiErrorDialog()
        {
            if (_isUiErrorDialogShown) return;
            _isUiErrorDialogShown = true;

            if (_window?.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    if (_window?.Content is FrameworkElement fe && fe.XamlRoot is { } root)
                    {
                        var dialog = new ContentDialog
                        {
                            Title = "发生异常",
                            Content = "本次界面操作可能未完成，你可以继续使用应用。若问题重复出现，请查看错误日志。",
                            CloseButtonText = "继续使用",
                            DefaultButton = ContentDialogButton.Close,
                            XamlRoot = root
                        };
                        await dialog.ShowAsync();
                    }
                }
#pragma warning disable CA1031 // 弹窗异常不应影响应用状态
                catch (Exception ex)
                {
                    Log.Warning(ex, "无法显示 UI 错误提示");
                }
#pragma warning restore CA1031
                finally { _isUiErrorDialogShown = false; }
            }) != true)
                _isUiErrorDialogShown = false;
        }
    }
}
