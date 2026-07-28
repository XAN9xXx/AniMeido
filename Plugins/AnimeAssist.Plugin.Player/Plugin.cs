using AniMeido.Contracts;
using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Player.Diagnostics;
using AniMeido.Plugin.Player.Playback;
using AniMeido.Plugin.Player.Services;
using AniMeido.Plugin.Player.Sources;
using AniMeido.Plugin.Player.Sources.EasyBangumi;
using AniMeido.Plugin.Player.Sources.Packages;
using AniMeido.Plugin.Player.Sources.Subscriptions;
using AniMeido.Plugin.Player.Sources.Web;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace AniMeido.Plugin.Player;

/// <summary>
/// Optional online anime player plugin.
/// </summary>
public sealed class PlayerPlugin : IPlugin
{
    private static readonly Assembly PluginAssembly =
        typeof(PlayerPlugin).Assembly;

    public string PluginID => PluginAssembly.GetName().Name
        ?? throw new InvalidOperationException(
            "PlayerPlugin 程序集缺少名称。");

    public string DisplayName =>
        PluginAssembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title
        ?? PluginID;

    public string Version =>
        PluginAssembly.GetName().Version?.ToString(3)
        ?? throw new InvalidOperationException(
            "PlayerPlugin 程序集缺少版本。");

    public bool IsRequired => false;

    public Task InitializeAsync(IServiceCollection services)
    {
        services.AddSingleton<HttpClient>();
        services.AddSingleton<SourcePackageInstaller>();
        services.AddSingleton<SourceMappingStore>();
        services.AddSingleton<EasyPreferenceStore>();
        services.AddSingleton<PlayerRuntimeSettingsStore>();
        services.AddSingleton<HostWebSessionManager>();
        services.AddSingleton<PlaybackDiagnosticRecorder>();
        services.AddSingleton<PlayerExperienceSettingsStore>();
        services.AddSingleton<WebMediaResolver>();
        services.AddSingleton<SourceSubscriptionService>();
        services.AddSingleton<OnlineSourceCatalog>();
        services.AddSingleton<PlayerWindowManager>();
        services.AddSingleton<IAnimePlaybackLauncher>(provider =>
            provider.GetRequiredService<PlayerWindowManager>());
        return Task.CompletedTask;
    }

    public IEnumerable<PluginNavigationItem> GetNavigationItems() => [];
}
