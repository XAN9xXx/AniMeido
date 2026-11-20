using AniMeido.Contracts;
using AniMeido.Contracts.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.ProbePlugin;

public sealed class ProbePlugin : IPlugin, IPluginSettingsLauncher, IDisposable
{
    private Window? _window;
    public string PluginID => "Probe";
    public string DisplayName => "Uno 宿主验证插件";
    public string Version => "1.0.0";
    public bool IsRequired => false;
    public IEnumerable<PluginNavigationItem> GetNavigationItems() => [];

    public Task InitializeAsync(IServiceCollection services)
    {
        services.AddSingleton<IPluginSettingsLauncher>(_ => this);
        return Task.CompletedTask;
    }

    public Task OpenSettingsAsync(string settingsId, CancellationToken cancellationToken = default)
    {
        if (settingsId == "open")
        {
            _window ??= new Window { Title = DisplayName, Content = new TextBlock { Text = DisplayName } };
            _window.Activate();
        }
        else if (settingsId == "close")
            Dispose();
        else
            throw new ArgumentOutOfRangeException(nameof(settingsId));
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _window?.Close();
        _window = null;
    }
}
