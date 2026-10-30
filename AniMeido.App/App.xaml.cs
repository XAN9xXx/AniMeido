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
        private readonly InstalledAppContext _installedApp;
        private readonly CancellationTokenSource _shutdownCancellation = new();
        private Task _themeStartup = Task.CompletedTask;
        private Task _trayStartup = Task.CompletedTask;
        private Task _dataStartup = Task.CompletedTask;
        private Task _startupNotices = Task.CompletedTask;
        private Task _updateStartup = Task.CompletedTask;

        public App()
        {
            try
            {
                StartupLogger.Initialize();
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // 数据路径无效时不能初始化日志，更不能回退后打开日常数据。
                GlobalExceptionHandler.NativeMethods.MessageBox(
                    IntPtr.Zero, ex.Message, "AniMeido 无法启动", 0x00000010);
                Environment.Exit(1);
            }
            _installedApp = new InstalledAppContext();
            var identityResult = _installedApp.SetNotificationIdentity();
            if (identityResult < 0)
            {
                Log.Warning("设置 AniMeido.Desktop AppUserModelID 失败：{HResult}", identityResult);
            }
            GlobalExceptionHandler.Register();
            UnhandledException += OnAppUnhandledException;
            InitializeComponent();
        }

        public static IServiceProvider? Services { get; private set; }
        public static Window? MainWindow { get; private set; }
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
            ShowRestoreNoticeBeforeDatabaseInitialization();
            var services = new ServiceCollection()
                .AddAppServices(_installedApp);

            var pluginPackageManager = PluginPackageManager.CreateDefault();
            services.AddSingleton(pluginPackageManager);

            // Base 插件在构建 ServiceProvider 前注册自己的服务。
            var (navItems, plugins) = await PluginStartup.LoadPluginsAsync(services);
            App.Plugins = plugins;

            var provider = services.BuildServiceProvider();
            _serviceProvider = provider;
            Services = provider;
            var notificationInitialization = provider
                .GetRequiredService<WindowsAppNotificationService>()
                .InitializeAsync();
            var databaseInitialization = DatabaseStartup.InitializeAsync(provider);
            var desktopSettingsLoad = LoadDesktopSettingsAsync(provider);
            await Task.WhenAll(
                notificationInitialization,
                databaseInitialization,
                desktopSettingsLoad);
            _desktopSettings = await desktopSettingsLoad;

            var contributionRegistry =
                provider.GetRequiredService<PluginContributionRegistry>();
            contributionRegistry.SetBuiltInItems(navItems);
            _window = new MainWindow(
                contributionRegistry,
                provider.GetRequiredService<NavigationService>(),
                provider.GetRequiredService<ThemeService>(),
                provider.GetRequiredService<StartupDialogCoordinator>(),
                new MainWindowDragDropAdapter(
                    () => provider.GetRequiredService<
                        AniMeido.Plugin.Base.Services.DragDropService>()),
                new StartupMigrationGate(ct => provider.GetRequiredService<
                    AniMeido.Plugin.Base.Services.PublicIdMigrationService>().MigrateAsync(ct)),
                provider.GetRequiredService<DatabaseService>().BackupDir);
            MainWindow = _window;

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
            // 数据库初始化已完成，主窗口已经 Activate；这不是首帧或全部后台功能的健康保证。
            MarkInstalledAppHealthy();
            _window.ShowLegacyInstallationNotice(provider.GetRequiredService<LegacyInstallationService>());
            _dataStartup = StartDataServicesWhenReadyAsync(provider, _window.DataReady.Task);
            if (_desktopSettings.KeepInTrayOnClose)
            {
                _trayStartup = StartTrayIconAtStartupAsync(provider);
                await _trayStartup;
            }

            if (_shutdownStarted)
            {
                return;
            }

            if (_window.Content is FrameworkElement root)
            {
                _themeStartup = provider.GetRequiredService<ThemeService>().InitializeThemeAsync(root);
                await _themeStartup;
            }
            if (_shutdownStarted)
            {
                return;
            }

            _startupNotices = ShowStartupNoticesAsync(provider, _shutdownCancellation.Token);
            _updateStartup = UpdateStartupTask.CheckForUpdateSilentlyAsync(
                provider, _window, _startupNotices, _shutdownCancellation.Token);
        }

        private static async Task<DesktopSettings> LoadDesktopSettingsAsync(IServiceProvider provider)
        {
            try
            {
                return await provider.GetRequiredService<LegacyDesktopSettingsImporter>().ImportAsync();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or System.Security.SecurityException)
            {
                Log.Warning(ex, "旧桌面设置尚未导入，下次启动重试");
                return await provider.GetRequiredService<DesktopSettingsStore>().LoadAsync();
            }
        }

        private void ShowRestoreNoticeBeforeDatabaseInitialization()
        {
            try
            {
                _installedApp.ShowRestoreNotice(notice =>
                {
                    var message = notice.Success
                        ? $"已从以下数据恢复：\n{notice.Directory}\n\n恢复前的当前数据已另存到：\n{notice.Archive}"
                        : $"未能恢复所选数据：\n{notice.Directory}\n\n原因：{notice.Error}"
                            + (notice.Archive is null ? "\n当前数据库未变。" : $"\n恢复前数据另存到：\n{notice.Archive}");
                    // 这时还没有 WinUI 窗口；先提示再初始化/迁移数据库，避免初始化失败吞掉结果。
                    if (GlobalExceptionHandler.NativeMethods.MessageBox(IntPtr.Zero, message,
                        notice.Success ? "AniMeido - 数据已恢复" : "AniMeido - 数据恢复失败",
                        notice.Success ? 0x00000040u : 0x00000030u) == 0)
                        throw new InvalidOperationException("无法显示数据恢复结果，结果已保留。");
                });
                _installedApp.ShowUnprocessedRestoreRequest(request =>
                {
                    var message = $"上次请求的数据恢复没有执行：\n{request.Directory}\n\n"
                        + "启动器可能版本过旧，不支持恢复。当前数据库未变；请安装最新版 AniMeido 后在设置页重新恢复。";
                    if (GlobalExceptionHandler.NativeMethods.MessageBox(IntPtr.Zero, message,
                        "AniMeido - 数据恢复未执行", 0x00000030u) == 0)
                        throw new InvalidOperationException("无法显示恢复未执行的提示，请求已保留。");
                });
            }
#pragma warning disable CA1031 // 结果提示失败保留通知，不阻止正常数据库初始化。
            catch (Exception ex)
            {
                Log.Warning(ex, "无法显示数据恢复结果");
            }
#pragma warning restore CA1031
        }

        private async Task StartDataServicesWhenReadyAsync(IServiceProvider provider, Task dataReady)
        {
            try
            {
                await dataReady.WaitAsync(_shutdownCancellation.Token);
                if (_shutdownStarted) return;
                await provider.GetRequiredService<AniMeido.Plugin.Base.Services.CacheService>().CleanExpiredAsync();
                _shutdownCancellation.Token.ThrowIfCancellationRequested();
                provider.GetRequiredService<AniMeido.Plugin.Base.Services.PublicIdCheckService>().Start();
                await provider.GetRequiredService<GlobalShortcutManager>().StartAsync(_shutdownCancellation.Token);
                await StartPluginHostSafelyAsync(provider.GetRequiredService<PluginHostSupervisor>(), _shutdownCancellation.Token);
            }
            catch (OperationCanceledException) when (_shutdownStarted) { }
#pragma warning disable CA1031 // 数据就绪后的后台初始化失败不能使可用窗口退出。
            catch (Exception ex) { Log.Warning(ex, "资料迁移后的后台服务初始化失败"); }
#pragma warning restore CA1031
        }

        private void MarkInstalledAppHealthy()
        {
            if (!_installedApp.IsInstalled)
            {
                return;
            }
            try
            {
                _installedApp.MarkHealthy();
                _serviceProvider!.GetRequiredService<InstallationRegistrationService>().UpdateDisplayVersion();
                // 更新服务等待这次启动清理结束，避免 zip 或刚解压的版本目录被并发清理。
                _installedApp.StartupCleanup = Task.Run(() =>
                {
                    try
                    {
                        foreach (var error in _installedApp.CleanupObsolete())
                        {
                            Log.Warning("无法清理旧版本 {Path}：{Message}", error.Path, error.Message);
                        }
                    }
#pragma warning disable CA1031 // 后台清理的意外异常也不能影响已显示的主窗口。
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "后台清理旧版本失败");
                    }
#pragma warning restore CA1031
                });
            }
#pragma warning disable CA1031 // 健康标记/清理失败不能影响主程序启动。
            catch (Exception ex)
            {
                Log.Warning(ex, "无法写入安装健康标记");
            }
#pragma warning restore CA1031
        }

        private async Task ShowStartupNoticesAsync(
            IServiceProvider provider,
            CancellationToken cancellationToken)
        {
            try
            {
                await provider.GetRequiredService<StartupDialogCoordinator>()
                    .WaitForCompletionAsync(cancellationToken);
                if (!_installedApp.IsInstalled)
                    return;

                if (_window?.Content is not FrameworkElement element || element.XamlRoot is not { } root)
                    return;

                await _installedApp.ShowRolledBackNoticeAsync(async notice =>
                {
                    var message = $"已恢复到上一版本 {_installedApp.Version}，新版本 {notice.Version} 未能正常启动。";
                    if (notice.Reason == AniMeido.Updates.Install.RolledBackNotice.UserRestored)
                    {
                        message += "\n新版本期间的数据已另存到 Backups\\update 的 AniMeido-rollback-from-* 目录。";
                    }
                    var dialog = new ContentDialog
                    {
                        Title = "已恢复上一版本",
                        Content = message,
                        CloseButtonText = "知道了",
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = root,
                    };
                    cancellationToken.ThrowIfCancellationRequested();
                    using var closing = cancellationToken.Register(() =>
                        dialog.DispatcherQueue.TryEnqueue(dialog.Hide));
                    await dialog.ShowAsync();
                    cancellationToken.ThrowIfCancellationRequested();
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
#pragma warning disable CA1031 // 回滚提示不能中断已经成功启动的应用。
            catch (Exception ex)
            {
                Log.Warning(ex, "无法显示版本回滚提示");
            }
#pragma warning restore CA1031
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
                && _desktopSettings.KeepInTrayOnClose
                && _serviceProvider?.GetRequiredService<TrayIconService>().IsRunning == true)
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
            _shutdownCancellation.Cancel();
            _window?.BeginShutdown();
            var restorePreparation = _installedApp.StopRestorePreparationAsync();
            try
            {
                var provider = _serviceProvider;
                _serviceProvider = null;
                if (provider is not null)
                {
                    await StopApplicationServicesAsync(provider, restorePreparation);
                }
            }
#pragma warning disable CA1031 // Shutdown cleanup failure must not trap the window open.
            catch (Exception ex)
            {
                Log.Error(ex, "应用关闭清理失败");
            }
#pragma warning restore CA1031
            finally
            {
                _shutdownCancellation.Dispose();
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

        private async Task StopApplicationServicesAsync(
            ServiceProvider provider,
            Task restorePreparation)
        {
            var notifications = provider.GetRequiredService<WindowsAppNotificationService>();
            var failures = new List<Exception>();
            try
            {
                await StopStartupTasksAsync(provider, restorePreparation, failures);
                await StopDataProducersAsync(provider, notifications, failures);
            }
#pragma warning disable CA1031 // 组合阶段失败仍要执行数据屏障，并在全部收尾后统一报告。
            catch (Exception ex)
            {
                failures.Add(ex);
            }
#pragma warning restore CA1031
            finally
            {
                try
                {
                    await PluginStartup.StopAfterDataDrainAsync(
                        Plugins ?? [],
                        provider.GetRequiredService<AniMeido.Plugin.Base.Services.SqliteConnectionFactory>());
                }
#pragma warning disable CA1031 // 插件资源在数据排空后停止，故障不阻止后续依赖释放。
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
#pragma warning restore CA1031

                try
                {
                    await notifications.DisposeAsync();
                }
#pragma warning disable CA1031 // 通知失败不阻止其余服务释放，原始异常由最终边界报告。
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
#pragma warning restore CA1031

                try
                {
                    await provider.DisposeAsync();
                }
#pragma warning disable CA1031 // DI 可能再次交还已观察的失败，仍保留本次真实收尾结果。
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
#pragma warning restore CA1031
            }

            if (failures.Count > 0)
            {
                var errors = new AggregateException(failures).Flatten().InnerExceptions.Distinct();
                throw new AggregateException("应用关闭清理未全部完成。", errors);
            }
        }

        private async Task StopStartupTasksAsync(
            ServiceProvider provider,
            Task restorePreparation,
            List<Exception> failures)
        {
            Task? completion = null;
            try
            {
                completion = Task.WhenAll(
                    _dataStartup, _startupNotices, _updateStartup, _trayStartup, _themeStartup,
                    restorePreparation,
                    provider.GetRequiredService<TrayIconService>().DisposeAsync().AsTask(),
                    _window?.WaitForStartupAsync() ?? Task.CompletedTask);
                await completion;
            }
#pragma warning disable CA1031 // 启动收尾失败也必须继续停止数据入口并排空已接纳写入。
            catch (Exception ex)
            {
                failures.Add(completion?.Exception ?? ex);
            }
#pragma warning restore CA1031
        }

        private static async Task StopDataProducersAsync(
            ServiceProvider provider,
            WindowsAppNotificationService notifications,
            List<Exception> failures)
        {
            // 宿主的依赖可能首次注册提醒回调，必须在封住通知入口之前完成组合。
            var supervisor = provider.GetRequiredService<PluginHostSupervisor>();
            var checks = provider.GetRequiredService<AniMeido.Plugin.Base.Services.PublicIdCheckService>();
            var shortcuts = provider.GetRequiredService<GlobalShortcutManager>();

            try
            {
                await notifications.StopActivationsAsync();
            }
#pragma warning disable CA1031 // 激活回调已结束，取消回调的失败不能阻止其他生产者停止。
            catch (Exception ex)
            {
                failures.Add(ex);
            }
#pragma warning restore CA1031

            Task? completion = null;
            try
            {
                completion = Task.WhenAll(
                    shortcuts.DisposeAsync().AsTask(),
                    checks.DisposeAsync().AsTask(),
                    supervisor.StopAndDrainAsync());
                await completion;
            }
#pragma warning disable CA1031 // 所有生产者的停止均已等待；失败不能跳过数据库永久停止屏障。
            catch (Exception ex)
            {
                failures.Add(completion?.Exception ?? ex);
            }
#pragma warning restore CA1031
        }

        internal async Task SetKeepInTrayOnCloseAsync(bool enabled)
        {
            var provider = _serviceProvider;
            if (provider is null)
            {
                return;
            }

            var trayIcon = provider.GetRequiredService<TrayIconService>();
            if (enabled)
            {
                await StartTrayIconAsync(provider);
            }
            else
            {
                await trayIcon.StopAsync();
            }

            try
            {
                var savedSettings = await provider.GetRequiredService<DesktopSettingsStore>()
                    .UpdateAsync(settings => settings with { KeepInTrayOnClose = enabled });
                _desktopSettings = savedSettings with
                {
                    KeepInTrayOnClose = savedSettings.KeepInTrayOnClose && trayIcon.IsRunning,
                };
            }
            catch
            {
                if (enabled)
                {
                    await trayIcon.StopAsync();
                }
                else if (_desktopSettings.KeepInTrayOnClose)
                {
                    await StartTrayIconAsync(provider);
                }

                throw;
            }
        }

        internal void RequestExit()
        {
            // 托盘“退出”和更新重启共用：触发 Closing，释放 DI/PluginHost，不走托盘隐藏分支。
            _exitRequested = true;
            _window?.Close();
        }

        private Task StartTrayIconAsync(IServiceProvider provider)
            => provider.GetRequiredService<TrayIconService>().StartAsync(
                () => provider
                    .GetRequiredService<AppWindowActivationService>()
                    .ActivateMainWindow(),
                RequestExit,
                OnTrayUnavailable);

        private void OnTrayUnavailable()
        {
            if (_shutdownStarted || _shutdownCompleted || _exitRequested)
                return;

            _desktopSettings = _desktopSettings with { KeepInTrayOnClose = false };
            _activationService?.ActivateMainWindow();
        }

        private async Task StartTrayIconAtStartupAsync(
            IServiceProvider provider)
        {
            try
            {
                await StartTrayIconAsync(provider);
            }
            catch (OperationCanceledException) when (_shutdownStarted)
            {
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
                    _desktopSettings = await provider.GetRequiredService<DesktopSettingsStore>()
                        .UpdateAsync(settings => settings with { KeepInTrayOnClose = false });
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
            PluginHostSupervisor supervisor,
            CancellationToken cancellationToken)
        {
            try
            {
                await supervisor.StartAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
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
