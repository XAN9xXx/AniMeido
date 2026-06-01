namespace AniMeido.Contracts.Models
{
    /// <summary>
    /// 人物（声优/制作人员）参与的作品。
    /// </summary>
    /// <param name="ID">作品条目 ID。</param>
    /// <param name="Title">作品标题。</param>
    /// <param name="Staff">该人物在此作品中的职责（如"配音"、"原作"）。</param>
    /// <param name="CoverURL">作品封面 URL。</param>
    public record PersonWork(int ID, string Title, string? Staff, string? CoverURL = null);
}
