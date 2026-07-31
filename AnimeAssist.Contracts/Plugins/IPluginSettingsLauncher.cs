namespace AniMeido.Contracts.Plugins;

/// <summary>
/// Opens a settings surface owned by a hosted plugin.
/// </summary>
public interface IPluginSettingsLauncher
{
    /// <summary>
    /// Opens the settings surface identified by <paramref name="settingsId"/>.
    /// </summary>
    Task OpenSettingsAsync(
        string settingsId,
        CancellationToken cancellationToken = default);
}
