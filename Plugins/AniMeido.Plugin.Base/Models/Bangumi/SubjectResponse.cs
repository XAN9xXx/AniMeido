namespace AniMeido.Plugin.Base.Models.Bangumi
{
    /// <param name="Score">加权评分（0-10）。</param>
    /// <param name="Total">评分人数。</param>
    internal record SubjectRating(double Score, int Rank, int Total);

    /// <param name="Name">番剧的日文原名。</param>
    /// <param name="Platform">Bangumi 维护的作品发行平台。</param>
    internal record SubjectResponse(
        int Id,
        string Name,
        string? NameCn,
        string? Summary,
        string? Date,
        string? Platform,
        ImageInfo? Images,
        List<string>? MetaTags,
        SubjectRating? Rating = null);

}
