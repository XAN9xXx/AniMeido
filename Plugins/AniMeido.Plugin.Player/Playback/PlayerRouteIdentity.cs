namespace AniMeido.Plugin.Player.Playback;

/// <summary>一次查找使用的作品身份；已应用的编号变化只调整后续写回位置。</summary>
internal sealed class PlayerRouteIdentity(long animeId)
{
    private long _currentAnimeId = animeId;
    private long _appliedRemapSeq;

    internal long OriginalAnimeId { get; } = animeId;
    internal long CurrentAnimeId => Interlocked.Read(ref _currentAnimeId);
    internal bool IsShadowed { get; private set; }
    internal long AppliedRemapSeq => Interlocked.Read(ref _appliedRemapSeq);

    internal void Publish(long currentAnimeId, bool isShadowed, long appliedRemapSeq)
    {
        Interlocked.Exchange(ref _currentAnimeId, currentAnimeId);
        IsShadowed = isShadowed;
        Interlocked.Exchange(ref _appliedRemapSeq, appliedRemapSeq);
    }
}

internal sealed record PlayerRoutePreparation(
    PlayerRouteIdentity Identity,
    PlayerExperienceReadResult ReadResult,
    PlayerPublicIdChanges Changes);
