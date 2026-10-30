namespace AniMeido.Contracts.Plugins;

/// <summary>
/// 打开由独立宿主中的插件持有的设置界面。
/// </summary>
public interface IPluginSettingsLauncher
{
    /// <summary>打开本插件内指定的设置入口，不返回或转移窗口所有权。</summary>
    Task OpenSettingsAsync(
        string settingsId,
        CancellationToken cancellationToken = default);
}
