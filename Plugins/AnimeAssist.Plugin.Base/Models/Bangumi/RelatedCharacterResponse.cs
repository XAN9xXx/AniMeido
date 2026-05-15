namespace AnimeAssist.Plugin.Base.Models.Bangumi
{
    /// <summary>
    /// 相关角色信息。
    /// </summary>
    /// <remarks>
    /// 参数列表：int Id, string Name, List'ActorInfo' Actors, string Summary, PersonImages Images, string Relation, List'CharacterRole' Roles
    /// </remarks>
    /// <param name="Id">角色的唯一标识符。</param>
    /// <param name="Name">角色的姓名。</param>
    /// <param name="Actors">配音演员列表。</param>
    /// <param name="Summary">角色的简介。</param>
    /// <param name="Images">角色的图片信息。</param>
    /// <param name="Relation">角色的定位（如主角，配角，闲角）。</param>
    internal record RelatedCharacterResponse(int Id, string Name, List<ActorInfo>? Actors, string? Summary, PersonImages? Images, string Relation);

}
