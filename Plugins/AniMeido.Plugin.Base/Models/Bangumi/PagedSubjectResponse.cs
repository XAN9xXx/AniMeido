namespace AniMeido.Plugin.Base.Models.Bangumi
{
    /// <summary>
    /// 以分页形式储存SubjectResponse
    /// </summary>
    /// <param name="Total">总条目数</param>
    /// <param name="Limit">每页条数</param>
    /// <param name="Offset">当前偏移</param>
    /// <param name="Data">当前页的SubjectResponse列表</param>
    internal record PagedSubjectResponse(
        int Total,
        int Limit,
        int Offset,
        List<SubjectResponse> Data
    );
}
