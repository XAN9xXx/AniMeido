namespace AniMeido.Plugin.Base.Models.Bangumi
{
    /// <param name="Relation">角色的定位（如主角，配角，闲角）。</param>
    internal record RelatedCharacterResponse(int Id, string Name, List<ActorInfo>? Actors, string? Summary, PersonImages? Images, string Relation);

}
