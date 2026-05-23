namespace AniMeido.Plugin.Base.Models.Bangumi
{
    /// <summary>
    /// 以分页形式储存SubjectResponse
    /// </summary>
    /// <param name="Data">要分为一页的SubjectResponse列表</param>
    internal record PagedSubjectResponse(List<SubjectResponse> Data);
}
