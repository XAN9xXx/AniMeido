namespace AniMeido.Plugin.Base.Models.Bangumi
{
    /// <summary>人物出演的角色及该角色所属的作品。</summary>
    internal record PersonCharacterResponse(
        int SubjectId,
        int SubjectType,
        string? SubjectName,
        string? SubjectNameCn,
        string? Staff);
}
