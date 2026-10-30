namespace AniMeido.Contracts.Models
{
    /// <summary>资料源的声优身份及展示信息；CoverURL 为空时没有头像地址。</summary>
    public record VoiceActor(int VoiceActorId, string Name, string? CoverURL);
}
