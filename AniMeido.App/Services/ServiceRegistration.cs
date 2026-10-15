using AniMeido.Contracts;
using AniMeido.Contracts.Playback;
using AniMeido.Contracts.Notifications;
using AniMeido.Contracts.Desktop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace AniMeido.App.Services;

internal static class ServiceRegistration
{
    public static IServiceCollection AddAppServices(this IServiceCollection services)
    {
        services.AddLogging(builder =>
        {
            builder.AddSerilog(dispose: true);
            builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning);
        });

        services.AddHttpClient();
        var updates = services.AddHttpClient(InstalledUpdateService.HttpClientName);
#if UPDATE_TEST_SOURCE
        updates.AddHttpMessageHandler(() => new UpdateTestSourceHandler());
#endif

        services.AddSingleton<IAppDataPaths, AppDataPaths>();
        services.AddSingleton(_ => new InstalledAppContext());
        services.AddSingleton<InstalledUpdateService>();
        services.AddSingleton<IInstallationRegistry, InstallationRegistry>();
        services.AddSingleton<InstallationRegistrationService>();
        services.AddSingleton<LegacyInstallationService>();
        services.AddSingleton<DatabaseService>();
        services.AddSingleton<UpdateService>(sp =>
            new UpdateService(sp.GetRequiredService<IHttpClientFactory>(), "https://animeido.com/version.json"));
        services.AddSingleton<PageFactory>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<PluginContributionRegistry>();
        services.AddSingleton<HostedAnimePlaybackLauncher>();
        services.AddSingleton<IAnimePlaybackLauncher>(provider =>
            provider.GetRequiredService<HostedAnimePlaybackLauncher>());
        services.AddSingleton<PluginHostSupervisor>();
        services.AddSingleton<IActiveAnimePlaybackContextProvider,
            HostedActivePlaybackContextProvider>();
        services.AddSingleton<ForegroundWindowCaptureService>();
        services.AddSingleton<IForegroundWindowCaptureService>(provider =>
            provider.GetRequiredService<ForegroundWindowCaptureService>());
        services.AddSingleton<AppWindowActivationService>();
        services.AddSingleton<IAppWindowActivationService>(provider =>
            provider.GetRequiredService<AppWindowActivationService>());
        services.AddSingleton<IWindowHandleProvider>(provider =>
            provider.GetRequiredService<AppWindowActivationService>());
        services.AddSingleton<GlobalShortcutManager>();
        services.AddSingleton<DesktopSettingsStore>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<WindowsAppNotificationService>();
        services.AddSingleton<IAppNotificationService>(provider =>
            provider.GetRequiredService<WindowsAppNotificationService>());
        services.AddSingleton<Contracts.IPluginNavigator>(sp => sp.GetRequiredService<NavigationService>());
        services.AddTransient<Views.SettingPage>();

        return services;
    }
}
