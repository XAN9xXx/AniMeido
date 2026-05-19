namespace AniMeido.Contracts.Models
{
    /// <summary>
    /// 表示一个动漫记录。
    /// </summary>
    /// <remarks>
    /// 参数列表：int ID, string Title, string? Studio, IReadOnlyList'VoiceActor' CVs, DateOnly? AirDate, string? CoverURL, string Description, int SeasonYear, int SeasonMonth
    /// </remarks>
    /// <param name="ID">动漫的唯一标识符。</param>
    /// <param name="Title">动漫的标题。</param>
    /// <param name="Studio">动漫的制作公司。</param>
    /// <param name="CVs">参与配音的声优列表。</param>
    /// <param name="AirDate">动漫的首播日期。</param>
    /// <param name="CoverURL">动漫的封面图片URL。</param>
    /// <param name="Description">动漫的简介。</param>
    /// <param name="SeasonYear">动漫所属的季度年份，例如2024。</param>
    /// <param name="SeasonMonth">动漫所属的季度月份，例如1=冬 4=春 7=夏 10=秋。</param>
    public record Anime(int ID, string Title, string? Studio, IReadOnlyList<VoiceActor> CVs, DateOnly? AirDate, string? CoverURL, string Description, int SeasonYear, int SeasonMonth, int? Weekday = null);
}
