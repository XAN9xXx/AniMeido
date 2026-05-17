using AnimeAssist.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AnimeAssist.App
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

        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
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
            var provider = services.BuildServiceProvider();
            Contracts.AppServices.Provider = provider;

            _window = new MainWindow(naviItems);
            if (_window.Content is FrameworkElement root)
                ThemeService.InitializeTheme(root);
            _window.Activate();

        }
    }
}
