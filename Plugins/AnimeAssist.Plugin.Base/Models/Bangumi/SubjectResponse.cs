namespace AniMeido.Plugin.Base.Models.Bangumi
{
    /// <summary>
    /// 番剧条目信息。
    /// </summary>
    /// <remarks>
    /// 参数列表：int Id, string Name, string? NameCn, string? Summary, string? Date, ImageInfo? Image, List'string'? MetaTags
    /// </remarks>
    /// <param name="Id">番剧的唯一标识符。</param>
    /// <param name="Name">番剧的日文原名。</param>
    /// <param name="NameCn">番剧的译名。</param>
    /// <param name="Summary">番剧的简介。</param>
    /// <param name="Date">番剧的放送日期。</param>
    /// <param name="Image">番剧封面图片的多种尺寸信息。</param>
    /// <param name="MetaTags">番剧的元标签列表。</param>
    internal record SubjectResponse(int Id, string Name, string? NameCn, string? Summary, string? Date, ImageInfo? Image, List<string>? MetaTags);

}
