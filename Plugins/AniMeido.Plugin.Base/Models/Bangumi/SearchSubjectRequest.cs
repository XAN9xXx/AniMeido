namespace AniMeido.Plugin.Base.Models.Bangumi
{
    /// <summary>
    /// POST /v0/search/subjects 的请求体。
    /// </summary>
    internal record SearchSubjectRequest(
        string? Keyword = null,
        string? Sort = null,
        SearchFilter? Filter = null
    );

    internal record SearchFilter(
        List<int>? Type = null,
        List<string>? Tag = null,
        List<string>? MetaTags = null,
        List<string>? AirDate = null
    );
}
