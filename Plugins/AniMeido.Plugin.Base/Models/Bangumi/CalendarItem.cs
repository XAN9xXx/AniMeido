namespace AniMeido.Plugin.Base.Models.Bangumi
{
    internal record CalendarItem
    {
        public int Id { get; init; }
        public string? Url { get; init; }
        /// <summary>每个条目的种类，此处（番剧）固定为2。</summary>
        public int Type { get; init; }
        /// <summary>番剧的日文原名。</summary>
        public string? Name { get; init; }
        public string? NameCn { get; init; }
        /// <summary>番剧的简介，新番条目一般为空。</summary>
        public string? Summary { get; init; }
        public string? AirDate { get; init; }
        /// <summary>番剧的放送星期（1=周一，7=周日）。</summary>
        public int AirWeekday { get; init; }
        public ImageInfo? Images { get; init; }
        public SubjectRating? Rating { get; init; }
    }

}
