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

        public App()
        {
            InitializeComponent();
        }

        public static IServiceProvider? Services { get; private set; }
        public static ThemeService ThemeService { get; } = new ThemeService();
        public static PrivacyService PrivacyService { get; } = new PrivacyService();
        public static IReadOnlyList<IPlugin>? Plugins { get; private set; }

        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Warning()
                .WriteTo.File("logs/aniMeido.log",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 3)
                .CreateLogger();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddHttpClient();
            var serviceProvider = services.BuildServiceProvider();
            var logger = serviceProvider.GetRequiredService<ILogger<PluginHost>>();
            var host = new PluginHost(services, logger);
            var pluginPath = AppContext.BaseDirectory + "Plugins";
            if (!Directory.Exists(pluginPath))
            {
                pluginPath = AppContext.BaseDirectory;
            }

            var naviItems = await host.LoadPluginAsync(pluginPath);
            App.Plugins = host.GetPlugins();
            services.AddSingleton<UpdateService>(sp =>
                new UpdateService(
                    sp.GetRequiredService<IHttpClientFactory>(),
                    "https://animeido.com/version.json"
                ));
            var provider = services.BuildServiceProvider();
            Contracts.AppServices.Provider = provider;
            Services = provider;

            _window = new MainWindow(naviItems);
            _window.Activate();
            if (_window.Content is FrameworkElement root)
                ThemeService.InitializeTheme(root);

            _ = CheckForUpdateSilentlyAsync(provider, _window);
        }

        private static async Task CheckForUpdateSilentlyAsync(IServiceProvider provider, Window window)
        {
            try
            {
                var updateService = provider.GetRequiredService<UpdateService>();
                var result = await updateService.CheckForUpdateAsync();

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
}
