namespace AniMeido.Contracts.Plugins;

/// <summary>Executes a command contribution owned by a hosted plugin.</summary>
public interface IPluginCommandLauncher
{
    Task InvokeCommandAsync(
        string commandId,
        CancellationToken cancellationToken = default);
}
