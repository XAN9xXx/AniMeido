using AniMeido.App.Services;
using AniMeido.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace AniMeido.App
{

    public partial class App : Application
    {
        private Window? _window;
        private bool _isRecoverableErrorShown;
        private ServiceProvider? _serviceProvider;

        public App()
        {
            InitializeComponent();
            GlobalExceptionHandler.Register();
            UnhandledException += OnAppUnhandledException;
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
#pragma warning disable CA1031 // 启动失败应显示错误对话框而非崩溃
            catch (Exception ex)
            {
                Log.Error(ex, "应用启动失败");
                ShowRecoverableErrorDialog($"应用启动失败: {ex.Message}");
            }
#pragma warning restore CA1031
        }

        private async Task InitializeApplicationAsync()
        {
            StartupLogger.Initialize();

            var services = new ServiceCollection()
                .AddAppServices();

            // 插件加载（在 ServiceProvider 构建前，因为 PluginHost 需要向 services 注册插件服务）
            var (navItems, plugins) = await PluginStartup.LoadPluginsAsync(
                services, AppContext.BaseDirectory);
            App.Plugins = plugins;

            // 构建 DI 容器并初始化数据库
            var provider = services.BuildServiceProvider();
            _serviceProvider = provider;
            Services = provider;
            await DatabaseStartup.InitializeAsync(provider);

            // 创建主窗口
            _window = new MainWindow(navItems, provider.GetRequiredService<NavigationService>());
            MainWindow = _window;
            Contracts.AppServices.MainWindow = _window;

            _window.Closed += (_, _) =>
            {
                _serviceProvider?.Dispose();
                Log.CloseAndFlush();
            };
            _window.Activate();

            if (_window.Content is FrameworkElement root)
                ThemeService.InitializeTheme(root);

            _ = UpdateStartupTask.CheckForUpdateSilentlyAsync(provider, _window);
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
                            _window?.Close();
                        }
                    }
                }
#pragma warning disable CA1031 // 弹窗异常不应影响应用状态
                catch { }
#pragma warning restore CA1031
                finally { _isRecoverableErrorShown = false; }
            });
        }
    }
}
