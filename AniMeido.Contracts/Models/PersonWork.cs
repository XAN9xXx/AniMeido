namespace AniMeido.Contracts.Models
{
    /// <summary>
    /// 人物（声优/制作人员）参与的作品。
    /// </summary>
    /// <param name="Staff">该人物在此作品中的职责（如"配音"、"原作"）。</param>
    /// <param name="Score">作品评分（0-10）；数据源没有提供时为 null。</param>
    public record PersonWork(
        int ID,
        string Title,
        string? Staff,
        string? CoverURL = null,
        double? Score = null,
        DateOnly? AirDate = null);
}
