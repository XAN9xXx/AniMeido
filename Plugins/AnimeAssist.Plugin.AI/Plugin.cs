using AniMeido.Contracts;
using AniMeido.Contracts.Plugins;
using AniMeido.Plugin.AI.Providers;
using AniMeido.Plugin.AI.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Windows.Input;

namespace AniMeido.Plugin.AI;

public sealed class AiPlugin : IPlugin
{
    private static readonly Assembly PluginAssembly = typeof(AiPlugin).Assembly;

    public string PluginID => PluginAssembly.GetName().Name
        ?? throw new InvalidOperationException("AI 插件程序集缺少名称。");

    public string DisplayName =>
        PluginAssembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title
        ?? PluginID;

    public string Version => PluginAssembly.GetName().Version?.ToString(3)
        ?? throw new InvalidOperationException("AI 插件程序集缺少版本。");

    public bool IsRequired => false;

    public Task InitializeAsync(IServiceCollection services)
    {
        services.AddSingleton<AiPluginPaths>();
        services.AddSingleton<AiSettingsStore>();
        services.AddSingleton<DpapiSecretStore>();
        services.AddSingleton<ConversationStore>();
        services.AddSingleton<HttpClient>();
        services.AddSingleton<OpenAiProviderAdapter>();
        services.AddSingleton<AnthropicProviderAdapter>();
        services.AddSingleton<GeminiProviderAdapter>();
        services.AddSingleton<OpenAiCompatibleProviderAdapter>();
        services.AddSingleton<AiProviderRouter>();
        services.AddSingleton<AiTaskCoordinator>();
        services.AddSingleton<AiWindowManager>();
        services.AddSingleton<IPluginCommandLauncher>(provider =>
            provider.GetRequiredService<AiWindowManager>());
        services.AddSingleton<IPluginSettingsLauncher>(provider =>
            provider.GetRequiredService<AiWindowManager>());
        return Task.CompletedTask;
    }

    public IEnumerable<PluginNavigationItem> GetNavigationItems()
    {
        yield return PluginNavigationItem.CreateCommand(
            "AI 工作台",
            "\uE945",
            AiWindowManager.OpenCommandId,
            new ServiceCommand(_ => { }));
    }

    private sealed class ServiceCommand(Action<object?> execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute(parameter);
    }
}
