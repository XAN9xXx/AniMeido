namespace AniMeido.App;

/// <summary>
/// 插件 ID 去重跟踪器，用于 PluginHost 内部维护已加载插件 ID 集合。
/// 提取为独立的可测试小类，避免在 PluginHost 中内联 HashSet 逻辑。
/// </summary>
internal sealed class PluginIdTracker
{
    private readonly HashSet<string> _ids = new(StringComparer.OrdinalIgnoreCase);

    public bool TryAdd(string pluginId) => _ids.Add(pluginId);
}
