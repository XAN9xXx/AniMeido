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
    public static IServiceCollection AddAppServices(
        this IServiceCollection services,
        InstalledAppContext? installedApp = null)
    {
        services.AddLogging(builder =>
        {
            // App 在全部服务收尾并报告结果后统一关闭全局日志。
            builder.AddSerilog(dispose: false);
            builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning);
        });

        services.AddHttpClient();
        var updates = services.AddHttpClient(InstalledUpdateService.HttpClientName);
#if UPDATE_TEST_SOURCE
        updates.AddHttpMessageHandler(() => new UpdateTestSourceHandler());
#endif

        services.AddSingleton<IAppDataPaths, AppDataPaths>();
        services.AddSingleton(_ => installedApp ?? new InstalledAppContext());
        services.AddSingleton<InstalledUpdateService>();
        services.AddSingleton<AppUpdateCheckService>();
        services.AddSingleton<RollbackDataStore>();
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
        services.AddSingleton<PluginManagementService>();
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
        services.AddSingleton<LegacyDesktopSettingsImporter>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<PrivacyService>();
        services.AddSingleton<StartupDialogCoordinator>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<WindowsAppNotificationService>();
        services.AddSingleton<IAppNotificationService>(provider =>
            provider.GetRequiredService<WindowsAppNotificationService>());
        services.AddSingleton<Contracts.IPluginNavigator>(sp => sp.GetRequiredService<NavigationService>());
        services.AddTransient<Views.SettingPage>();

        return services;
    }
}
