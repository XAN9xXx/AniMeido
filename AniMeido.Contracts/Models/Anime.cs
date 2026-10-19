namespace AniMeido.Contracts.Models
{
    /// <param name="ID">资料服务的作品公共 ID。</param>
    /// <param name="Studio">动漫的出版社/企划。</param>
    /// <param name="CVs">参与配音的声优列表。</param>
    /// <param name="SeasonMonth">动漫所属的季度月份，例如1=冬 4=春 7=夏 10=秋。</param>
    /// <param name="Score">动漫的评分（0-10）。</param>
    /// <param name="AlternateTitles">用于跨数据源匹配的其他官方标题。</param>
    public record Anime(
        int ID,
        string Title,
        string? Studio,
        IReadOnlyList<VoiceActor> CVs,
        DateOnly? AirDate,
        string? CoverURL,
        string Description,
        int SeasonYear,
        int SeasonMonth,
        int? Weekday = null,
        double? Score = null,
        IReadOnlyList<string>? AlternateTitles = null,
        AnimeMediaFormat MediaFormat = AnimeMediaFormat.Unknown);
}
