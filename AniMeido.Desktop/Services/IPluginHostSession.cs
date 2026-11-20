using AniMeido.Contracts.Playback;
using AniMeido.PluginProtocol;

namespace AniMeido.App.Services;

internal interface IPluginHostSession : IAsyncDisposable
{
    string PluginId { get; }
    string DisplayName { get; }
    bool IsRunning { get; }
    event EventHandler<PluginHostSessionExitedEventArgs>? Exited;
    Task<PluginHostSnapshot> StartAsync(CancellationToken cancellationToken = default);
    Task<bool> HasActiveUiAsync(CancellationToken cancellationToken = default);
    Task InvokeCommandAsync(string commandId, CancellationToken cancellationToken = default);
    Task OpenSettingsAsync(string settingsId, CancellationToken cancellationToken = default);
    Task LaunchAnimePlaybackAsync(AnimePlaybackRequest request, CancellationToken cancellationToken);
    Task<HostedActivePlaybackContext?> GetActiveContextAsync(CancellationToken cancellationToken = default);
}
