namespace AnimeAssist.Contracts.Models
{
    /// <summary>
    /// 角色-声优对照信息。
    /// </summary>
    /// <remarks>
    /// 参数列表：int CharacterId, string CharacterName, string? CharacterSummary, string? CharacterImage, List'VoiceActor' Actors
    /// </remarks>
    /// <param name="CharacterId">角色的唯一标识符。</param>
    /// <param name="CharacterName">角色的姓名。</param>
    /// <param name="CharacterSummary">角色的简介。</param>
    /// <param name="CharacterImage">角色的图片URL。</param>
    /// <param name="Actors">配音演员。</param>
    public record CharacterRole(int CharacterId, string CharacterName, string? CharacterSummary, string? CharacterImage, IReadOnlyList<VoiceActor> Actors);
}
