namespace AnimeAssist.Plugin.Base.Models.Bangumi
{
    /// <summary>
    /// 标注了每个新番条目的详细信息。
    /// </summary>
    /// <remarks>
    /// 参数列表：int Id, string? Url, int Type, string? Name, string? NameCn, string? Summary, string? AirDate, int AirWeekday, ImageInfo? Image
    /// </remarks>
    internal record CalendarItem
    {
        /// <summary>每部番剧的唯一标识符。</summary>
        public int Id { get; init; }
        /// <summary>番剧条目的URL。</summary>
        public string? Url { get; init; }
        /// <summary>每个条目的种类，此处（番剧）固定为2。</summary>
        public int Type { get; init; }
        /// <summary>番剧的日文原名。</summary>
        public string? Name { get; init; }
        /// <summary>番剧的译名。</summary>
        public string? NameCn { get; init; }
        /// <summary>番剧的简介，新番条目一般为空。</summary>
        public string? Summary { get; init; }
        /// <summary>番剧的放送日期。</summary>
        public string? AirDate { get; init; }
        /// <summary>番剧的放送星期（1=周一，7=周日）。</summary>
        public int AirWeekday { get; init; }
        /// <summary>番剧封面图片的多种尺寸信息。</summary>
        public ImageInfo? Image { get; init; }
    }

}
