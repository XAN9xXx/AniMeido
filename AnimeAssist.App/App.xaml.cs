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
            var provider = services.BuildServiceProvider();
            Contracts.AppServices.Provider = provider;

            _window = new MainWindow(naviItems);
            _window.Activate();
            if (_window.Content is FrameworkElement root)
                ThemeService.InitializeTheme(root);
        }
    }
}
