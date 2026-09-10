namespace AniMeido.Plugin.Player.Models;

internal enum PlaybackViewState
{
    Idle,
    Resolving,
    Buffering,
    Playing,
    Paused,
    Ended,
    Failed,
}
