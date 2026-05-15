namespace AnimeAssist.Contracts.Models
{
    /// <summary>
    /// 表示一个声优。
    /// </summary>
    /// <remarks>
    /// 参数列表：int VoiceActorId, string Name, string? CoverURL
    /// </remarks>
    /// <param name="VoiceActorId">声优的唯一标识符。</param>
    /// <param name="Name">声优的姓名。</param>
    /// <param name="CoverURL">声优的头像图片URL。</param>
    public record VoiceActor(int VoiceActorId, string Name, string? CoverURL);
}
