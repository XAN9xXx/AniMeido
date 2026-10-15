namespace AniMeido.Contracts.Plugins;

/// <summary>
/// Opens a settings surface owned by a hosted plugin.
/// </summary>
public interface IPluginSettingsLauncher
{
    Task OpenSettingsAsync(
        string settingsId,
        CancellationToken cancellationToken = default);
}
