namespace AniMeido.Plugin.Base.Models.Bangumi
{
    internal record PagedSubjectResponse(
        int Total,
        int Limit,
        int Offset,
        List<SubjectResponse> Data
    );
}
