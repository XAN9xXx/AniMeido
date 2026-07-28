using AniMeido.Contracts.Playback;

namespace AniMeido.Plugin.Player.Sources.Managed;

/// <summary>
/// Keeps the source load context alive while forwarding the source contract.
/// </summary>
internal sealed class ManagedSourceProxy : IOnlineAnimeSource
{
    private readonly ManagedSourceLoadContext _loadContext;
    private readonly IOnlineAnimeSource _source;

    public ManagedSourceProxy(
        ManagedSourceLoadContext loadContext,
        IOnlineAnimeSource source)
    {
        _loadContext = loadContext;
        _source = source;
    }

    public string Id => _source.Id;

    public string DisplayName => _source.DisplayName;

    public async Task<IReadOnlyList<SourceEpisode>> GetEpisodesAsync(
        AnimePlaybackContext anime,
        CancellationToken cancellationToken)
    {
        var result = await _source.GetEpisodesAsync(anime, cancellationToken);
        GC.KeepAlive(_loadContext);
        return result;
    }

    public async Task<ResolvedMedia> ResolveAsync(
        SourceEpisode episode,
        CancellationToken cancellationToken)
    {
        var result = await _source.ResolveAsync(episode, cancellationToken);
        GC.KeepAlive(_loadContext);
        return result;
    }
}
