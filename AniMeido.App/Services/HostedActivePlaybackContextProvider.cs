using AniMeido.Contracts.Playback;
using AniMeido.PluginProtocol;
using Microsoft.Extensions.Logging;

namespace AniMeido.App.Services;

public sealed class HostedActivePlaybackContextProvider :
    IActiveAnimePlaybackContextProvider
{
    private readonly PluginHostSupervisor _supervisor;
    private readonly ILogger<HostedActivePlaybackContextProvider> _logger;

    public HostedActivePlaybackContextProvider(
        PluginHostSupervisor supervisor,
        ILogger<HostedActivePlaybackContextProvider> logger)
    {
        _supervisor = supervisor;
        _logger = logger;
    }

    public async Task<ActiveAnimePlaybackContext?> GetActiveContextAsync(
        CancellationToken cancellationToken = default)
    {
        HostedActivePlaybackContext? context;
        try
        {
            context = await _supervisor.GetActivePlaybackContextAsync(
                cancellationToken);
        }
        catch (Exception ex) when (
            ex is IOException
                or TimeoutException
                or InvalidOperationException)
        {
            _logger.LogDebug(
                ex,
                "Optional playback context is unavailable.");
            return null;
        }
        return context is null
            ? null
            : new ActiveAnimePlaybackContext(
                context.AnimeId,
                context.Title,
                context.EpisodeNumber,
                context.PositionSeconds,
                context.ObservedAt);
    }
}
