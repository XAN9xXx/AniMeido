namespace AnimeAssist.Plugin.Base.Models.Bangumi
{
    /// <summary>
    /// 公司或重要STAFF信息。
    /// </summary>
    /// <remarks>
    /// 参数列表：int Id, string Name, string Relation, int Type, PersonImages Images
    /// </remarks>
    /// <param name="Id">公司或重要STAFF的唯一标识符。</param>
    /// <param name="Name">公司或重要STAFF的姓名。</param>
    /// <param name="Relation">公司或重要STAFF的职责。</param>
    /// <param name="Type">公司或重要STAFF的类型标识符。</param>
    /// <param name="Images">公司或重要STAFF的头像图片信息。</param>
    internal record RelatedPersonResponse(int Id, string Name, string Relation, int Type, PersonImages? Images);

}
