using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Player.Sources;

namespace AniMeido.Plugin.Player.Models;

internal sealed class PlaybackSession
{
    public AnimePlaybackContext AnimeContext { get; private set; }

    public SourceEpisodeEntry? Episode { get; private set; }

    public ResolvedMedia? Media { get; private set; }

    public PlaybackSession(AnimePlaybackContext animeContext)
    {
        ArgumentNullException.ThrowIfNull(animeContext);
        AnimeContext = animeContext;
    }

    public void ChangeAnime(AnimePlaybackContext animeContext)
    {
        ArgumentNullException.ThrowIfNull(animeContext);
        AnimeContext = animeContext;
        Episode = null;
        Media = null;
    }

    public void SelectEpisode(SourceEpisodeEntry episode)
    {
        ArgumentNullException.ThrowIfNull(episode);
        Episode = episode;
        Media = null;
    }

    public void SetResolvedMedia(ResolvedMedia media)
    {
        ArgumentNullException.ThrowIfNull(media);
        Media = media;
    }

    public void ClearResolvedMedia() => Media = null;
}
