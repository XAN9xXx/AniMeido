using AniMeido.App.Services;
using AniMeido.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using Serilog.Sinks.File;

namespace AniMeido.App
{

    public partial class App : Application
    {
        private Window? _window;
        private bool _isRecoverableErrorShown;

        public App()
        {
            InitializeComponent();

            // 全局未处理异常捕获
            AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            UnhandledException += OnAppUnhandledException;
        }

        public static IServiceProvider? Services { get; private set; }
        public static ThemeService ThemeService { get; } = new ThemeService();
        public static PrivacyService PrivacyService { get; } = new PrivacyService();
        public static IReadOnlyList<IPlugin>? Plugins { get; private set; }
        public static string? LatestVersion { get; private set; }

        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            // 配置 Serilog — 输出到 AppData/Roaming/AniMeido/logs/
            var logDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AniMeido", "logs");
            System.IO.Directory.CreateDirectory(logDir);
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Warning()
                .WriteTo.File(System.IO.Path.Combine(logDir, "aniMeido.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 3)
                .CreateLogger();

            var services = new ServiceCollection();
            services.AddLogging(builder =>
            {
                builder.AddSerilog(dispose: true);
                builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning);
            });
            services.AddHttpClient();
            var serviceProvider = services.BuildServiceProvider();
            var logger = serviceProvider.GetRequiredService<ILogger<PluginHost>>();
            var host = new PluginHost(services, logger);
            var naviItems = await host.LoadPluginAsync(AppContext.BaseDirectory);
            App.Plugins = host.GetPlugins();
            services.AddSingleton<DatabaseService>();
            services.AddSingleton(sp => sp.GetRequiredService<DatabaseService>().DbPath);
            services.AddSingleton<UpdateService>(sp =>
                new UpdateService(
                    sp.GetRequiredService<IHttpClientFactory>(),
                    "https://animeido.com/version.json"
                ));

            var provider = services.BuildServiceProvider();
            Contracts.AppServices.Provider = provider;
            Services = provider;
            var db = provider.GetRequiredService<DatabaseService>();
            await db.InitializeAsync();

            _window = new MainWindow(naviItems);

            // 窗口关闭时清理日志，进程退出由全局异常处理器统一处理
            _window.Closed += (_, _) =>
            {
                Log.CloseAndFlush();
            };

            _window.Activate();
            if (_window.Content is FrameworkElement root)
                ThemeService.InitializeTheme(root);

            _ = CheckForUpdateSilentlyAsync(provider, _window);
        }

        private void OnCurrentDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            var message = $"[AppDomain] 未处理异常: {ex?.Message}";
            Log.Fatal(ex, "[AppDomain] 未处理异常");

            if (e.IsTerminating)
            {
                ShowFatalErrorDialog(message);
                Environment.Exit(1);
            }
            else
            {
                ShowRecoverableErrorDialog(message);
            }
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            var ex = e.Exception?.InnerException ?? e.Exception;
            Log.Error(ex, "[Task] 未观察任务异常");
            e.SetObserved();
            ShowRecoverableErrorDialog($"[Task] 后台任务异常: {ex?.Message}");
        }

        private void OnAppUnhandledException(object? sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            Log.Error(e.Exception, "[UI] WinUI 未处理异常");
            ShowRecoverableErrorDialog($"[UI] 界面异常: {e.Exception.Message}");
            e.Handled = true;
        }

        private void ShowRecoverableErrorDialog(string message)
        {
            if (_isRecoverableErrorShown) return;
            _isRecoverableErrorShown = true;

            _ = _window?.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    if (_window?.Content is FrameworkElement fe && fe.XamlRoot is { } root)
                    {
                        var dialog = new ContentDialog
                        {
                            Title = "发生异常",
                            Content = $"AniMeido 遇到了一个可恢复的异常，应用可能部分功能不可用。\n\n{message}\n\n日志已保存到 AppData/Roaming/AniMeido/logs/",
                            PrimaryButtonText = "重新加载",
                            CloseButtonText = "继续使用",
                            DefaultButton = ContentDialogButton.Close,
                            XamlRoot = root
                        };
                        var result = await dialog.ShowAsync();
                        if (result == ContentDialogResult.Primary)
                        {
                            _isRecoverableErrorShown = false;
                            // 重新启动应用
                            _window?.Close();
                        }
                    }
                }
                catch
                {
                    // 对话框显示失败时忽略
                }
                finally
                {
                    _isRecoverableErrorShown = false;
                }
            });
        }

        private void ShowFatalErrorDialog(string message)
        {
            try
            {
                Log.Fatal("应用程序将因不可恢复异常退出: {Message}", message);
                Log.CloseAndFlush();

                var hWnd = _window != null
                    ? WinRT.Interop.WindowNative.GetWindowHandle(_window)
                    : IntPtr.Zero;
                _ = NativeMethods.MessageBox(hWnd, message, "AniMeido - 不可恢复错误", 0x00000010);
            }
            catch
            {
            }
        }

        private static async Task CheckForUpdateSilentlyAsync(IServiceProvider provider, Window window)
        {
            try
            {
                var updateService = provider.GetRequiredService<UpdateService>();
                var result = await updateService.CheckForUpdateAsync();

                if (result != null)
                    LatestVersion = result.LatestVersion;

                if (result?.HasUpdate == true)
                {
                    window.DispatcherQueue.TryEnqueue(async () =>
                    {
                        if (window.Content is FrameworkElement fe && fe.XamlRoot is { } xamlRoot)
                        {
                            var dialog = new ContentDialog
                            {
                                Title = "发现新版本",
                                Content = $"最新版本：{result.LatestVersion}\n\n{result.ReleaseNotes}\n\n如果下载缓慢，请尝试使用 Motrix 等工具加速下载。",
                                PrimaryButtonText = "下载更新",
                                CloseButtonText = "稍后再说",
                                DefaultButton = ContentDialogButton.Primary,
                                XamlRoot = xamlRoot
                            };

                            var dialogResult = await dialog.ShowAsync();
                            if (dialogResult == ContentDialogResult.Primary && result.DownloadUrl != null)
                            {
                                await Windows.System.Launcher.LaunchUriAsync(new Uri(result.DownloadUrl));
                            }
                        }
                    });
                }
            }
            catch
            {
                // 静默失败，不干扰用户
            }
        }
    }

    internal static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        internal static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
    }
}
