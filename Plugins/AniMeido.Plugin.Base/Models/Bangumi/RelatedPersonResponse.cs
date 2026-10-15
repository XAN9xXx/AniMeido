namespace AniMeido.Plugin.Base.Models.Bangumi
{
    /// <summary>
    /// 公司或重要STAFF信息。
    /// </summary>
    internal record RelatedPersonResponse(int Id, string Name, string Relation, int Type, PersonImages? Images);

}
