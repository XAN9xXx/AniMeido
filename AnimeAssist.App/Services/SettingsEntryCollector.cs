using AniMeido.Contracts;

namespace AniMeido.App.Services;

/// <summary>
/// 设置页条目收集器：从插件导航项中筛选出设置页并转换为 PluginSettingsEntry。
/// 提取为独立可测试类，避免在 SettingPage 中内联聚合逻辑。
/// </summary>
internal static class SettingsEntryCollector
{
    /// <summary>遍历所有插件，收集其设置页导航项。</summary>
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

/// <summary>
/// 设置页条目记录，标识设置项所属的插件和页面类型。
/// </summary>
public sealed record PluginSettingsEntry(
    string PluginId,
    string PluginDisplayName,
    string Label,
    string Icon,
    Type PageType);
