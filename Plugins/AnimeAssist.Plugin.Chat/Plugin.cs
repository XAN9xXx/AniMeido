using AniMeido.Contracts;
using AniMeido.Plugin.Chat.Services;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace AniMeido.Plugin.Chat;

/// <summary>
/// AniMeido 聊天室插件。提供独立的聊天室窗口，支持真实登录、多房间、文本消息通信。
/// </summary>
public class ChatPlugin : IPlugin
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
        // 聊天室入口为 Command 类型，不进入主窗口 Frame 导航
        var command = new RelayCommand(() => _windowManager?.OpenOrActivate());

        yield return PluginNavigationItem.CreateCommand("聊天室", "\uE704", command);
    }
}
