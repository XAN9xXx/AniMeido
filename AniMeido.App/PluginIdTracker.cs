namespace AniMeido.App;

internal sealed class PluginIdTracker
{
    private readonly HashSet<string> _ids = new(StringComparer.OrdinalIgnoreCase);

    public bool TryAdd(string pluginId) => _ids.Add(pluginId);

    public bool Remove(string pluginId) => _ids.Remove(pluginId);
}
