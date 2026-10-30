namespace AniMeido.Contracts.Playback;

/// <summary>当前可取得的播放会话快照，不依赖来源或播放器类型，也不保证后续仍然活跃。</summary>
/// <param name="EpisodeNumber">已知的播放集数；尚未取得时为 null。</param>
/// <param name="PositionSeconds">已知的播放位置，单位为秒；尚未取得时为 null。</param>
/// <param name="ObservedAt">播放状态的观测时间，不是本次查询的完成时间。</param>
public sealed record ActiveAnimePlaybackContext(
    int AnimeId,
    string Title,
    int? EpisodeNumber,
    double? PositionSeconds,
    DateTimeOffset ObservedAt);

/// <summary>读取已有的活跃播放上下文，不负责启动可选播放器。</summary>
public interface IActiveAnimePlaybackContextProvider
{
    /// <summary>返回可取得的会话快照；没有活跃会话或可选播放能力不可用时返回 null。</summary>
    Task<ActiveAnimePlaybackContext?> GetActiveContextAsync(
        CancellationToken cancellationToken = default);
}
