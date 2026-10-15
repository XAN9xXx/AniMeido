namespace AniMeido.Plugin.Base.Models.Bangumi
{
    /// <summary>
    /// 人物参与的作品条目信息（GET /v0/persons/{id}/subjects 返回）。
    /// </summary>
    /// <param name="Name">条目名称（日文原名）。</param>
    /// <param name="Type">条目类型（2=动画 3=音乐 4=游戏 6=现实）。</param>
    /// <param name="Eps">参与章节。</param>
    /// <param name="Rating">评分；本地 Archive 提供，在线接口没有这一项。</param>
    /// <param name="Date">放送日期；本地 Archive 提供，在线接口没有这一项。</param>
    internal record RelatedSubjectResponse(
        int Id,
        string Name,
        string? NameCn,
        int Type,
        string? Staff,
        string? Eps,
        string? Image,
        SubjectRating? Rating = null,
        string? Date = null);
}
