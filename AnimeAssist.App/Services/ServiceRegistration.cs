using AniMeido.Contracts;
using AniMeido.Contracts.Playback;
using AniMeido.Contracts.Notifications;
using AniMeido.Contracts.Desktop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace AniMeido.App.Services;

/// <summary>
/// 核心服务注册。集中管理所有 App 级 DI 注册，减少 App.xaml.cs 的职责。
/// </summary>
internal static class ServiceRegistration
{
    /// <summary>注册所有 App 核心服务。</summary>
    public static IServiceCollection AddAppServices(this IServiceCollection services)
    {
        services.AddLogging(builder =>
        {
            builder.AddSerilog(dispose: true);
            builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning);
        });

        services.AddHttpClient();

        services.AddSingleton<IAppDataPaths, AppDataPaths>();
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
