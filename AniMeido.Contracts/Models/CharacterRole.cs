namespace AniMeido.Contracts.Models
{
    /// <summary>
    /// 角色-声优对照信息。
    /// </summary>
    public record CharacterRole(int CharacterId, string CharacterName, string? CharacterSummary, string? CharacterImage, IReadOnlyList<VoiceActor> Actors);
}
