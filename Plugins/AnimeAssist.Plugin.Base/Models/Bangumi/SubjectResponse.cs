namespace AniMeido.Plugin.Base.Models.Bangumi
{
    /// <summary>
    /// Bangumi 评分数据。
    /// </summary>
    /// <param name="Score">加权评分（0-10）。</param>
    /// <param name="Rank">排名。</param>
    /// <param name="Total">评分人数。</param>
    internal record SubjectRating(double Score, int Rank, int Total);

    /// <summary>
    /// 番剧条目信息。
    /// </summary>
    /// <param name="Id">番剧的唯一标识符。</param>
    /// <param name="Name">番剧的日文原名。</param>
    /// <param name="NameCn">番剧的译名。</param>
    /// <param name="Summary">番剧的简介。</param>
    /// <param name="Date">番剧的放送日期。</param>
    /// <param name="Images">番剧封面图片的多种尺寸信息。</param>
    /// <param name="MetaTags">番剧的元标签列表。</param>
    /// <param name="Rating">番剧的评分数据。</param>
    internal record SubjectResponse(int Id, string Name, string? NameCn, string? Summary, string? Date, ImageInfo? Images, List<string>? MetaTags, SubjectRating? Rating = null);

}
