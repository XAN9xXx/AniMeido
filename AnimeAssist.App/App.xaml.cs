using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace AnimeAssist.App
{

    public partial class App : Application
    {
        private Window? _window;

        public App()
        {
            InitializeComponent();
        }


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

            _window = new MainWindow(naviItems);
            _window.Activate();
        }
    }
}
