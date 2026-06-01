namespace AniMeido.Plugin.Base.Models.Bangumi
{
    /// <summary>
    /// 人物参与的作品条目信息（GET /v0/persons/{id}/subjects 返回）。
    /// </summary>
    /// <param name="Id">条目 ID。</param>
    /// <param name="Name">条目名称（日文原名）。</param>
    /// <param name="NameCn">条目中文名。</param>
    /// <param name="Type">条目类型（2=动画 3=音乐 4=游戏 6=现实）。</param>
    /// <param name="Staff">该人物在此作品中的职责。</param>
    /// <param name="Eps">参与章节。</param>
    /// <param name="Image">条目封面图片 URL。</param>
    internal record RelatedSubjectResponse(int Id, string Name, string? NameCn, int Type, string? Staff, string? Eps, string? Image);
}
