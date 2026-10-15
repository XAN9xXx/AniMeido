using AniMeido.Contracts;

namespace AniMeido.App.Services;

internal static class SettingsEntryCollector
{
    public static List<PluginSettingsEntry> Collect(IReadOnlyList<IPlugin> plugins)
    {
        var entries = new List<PluginSettingsEntry>();

        foreach (var plugin in plugins)
        {
            foreach (var item in plugin.GetNavigationItems())
            {
                if (!item.IsSettingsPage || item.PageType is null)
                    continue;

                entries.Add(new PluginSettingsEntry(
                    plugin.PluginID,
                    plugin.DisplayName,
                    item.Label,
                    item.Icon,
                    item.PageType));
            }
        }

        return entries;
    }
}

public sealed record PluginSettingsEntry(
    string PluginId,
    string PluginDisplayName,
    string Label,
    string Icon,
    Type PageType);
