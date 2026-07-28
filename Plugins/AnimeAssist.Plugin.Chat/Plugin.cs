using AniMeido.Contracts;
using AniMeido.Plugin.Chat.Commands;
using AniMeido.Plugin.Chat.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AniMeido.Plugin.Chat;

/// <summary>
/// Optional local ChatPlugin prototype.
/// </summary>
public sealed class ChatPlugin : IPlugin
{
    private ChatWindowManager? _windowManager;

    public string PluginID => "AniMeido.Plugin.Chat";

    public string DisplayName => "聊天室";

    public string Version => "0.1.0";

    public bool IsRequired => false;

    public Task InitializeAsync(IServiceCollection services)
    {
        _windowManager = new ChatWindowManager();
        services.AddSingleton(_windowManager);
        return Task.CompletedTask;
    }

    public IEnumerable<PluginNavigationItem> GetNavigationItems()
    {
        var command = new DelegateCommand(
            () => _windowManager?.OpenOrActivate());
        yield return PluginNavigationItem.CreateCommand(
            "聊天室",
            "\uE8BD",
            "AniMeido.Plugin.Chat.open",
            command);
    }
}
