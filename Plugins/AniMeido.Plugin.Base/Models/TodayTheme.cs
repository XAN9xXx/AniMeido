namespace AniMeido.Plugin.Base.Models
{
    /// <summary>
    /// 今天页“今日主题”的九个主题。名称会存进本地配置，改名时要兼容旧值。
    /// </summary>
    public enum TodayThemeKind
    {
        RevisitedBrowse,
        LastSeasonTop,
        UnratedThisSeason,
        FavoriteGenre,
        OneYearAgoSeason,
        Movies,
        OneYearAgoToday,
        SameStudio,
        Stalled,
    }

    /// <summary>某一天显示的主题与这一批作品，同一天重启后按它恢复。</summary>
    /// <param name="Date">日期。</param>
    /// <param name="Theme">实际显示的主题（替补时是替补主题）。</param>
    /// <param name="IsFallback">当天轮到的主题没有内容，改用了替补。</param>
    /// <param name="AnimeIds">当前这一批作品。</param>
    public sealed record TodayThemeState(
        DateOnly Date,
        TodayThemeKind Theme,
        bool IsFallback,
        IReadOnlyList<int> AnimeIds);

    /// <summary>主题的数据从哪里来。</summary>
    internal enum TodayThemeSource
    {
        /// <summary>只用 Bangumi 数据（标记只用来排除已标记的作品）。</summary>
        Bangumi,
        /// <summary>只用你的本地记录。</summary>
        Personal,
        /// <summary>从你的记录出发，再到 Bangumi 找作品。</summary>
        Mixed,
    }

    /// <summary>主题的名称与数据来源。</summary>
    internal static class TodayThemeCatalog
    {
        /// <summary>当天轮到的主题没有内容时改用它。</summary>
        public const TodayThemeKind Fallback = TodayThemeKind.LastSeasonTop;

        public static string GetTitle(TodayThemeKind kind) => kind switch
        {
            TodayThemeKind.RevisitedBrowse => "你看过好几次的",
            TodayThemeKind.LastSeasonTop => "上一季的高分作品",
            TodayThemeKind.UnratedThisSeason => "本季还没评分的",
            TodayThemeKind.FavoriteGenre => "你偏爱题材的高分作品",
            TodayThemeKind.OneYearAgoSeason => "一年前的这一季",
            TodayThemeKind.Movies => "挑一部剧场版",
            TodayThemeKind.OneYearAgoToday => "一年前的今天",
            TodayThemeKind.SameStudio => "同一家制作公司",
            _ => "搁置最久的在看",
        };

        public static TodayThemeSource GetSource(TodayThemeKind kind) => kind switch
        {
            TodayThemeKind.RevisitedBrowse
                or TodayThemeKind.UnratedThisSeason
                or TodayThemeKind.OneYearAgoToday
                or TodayThemeKind.Stalled => TodayThemeSource.Personal,
            TodayThemeKind.FavoriteGenre
                or TodayThemeKind.SameStudio => TodayThemeSource.Mixed,
            _ => TodayThemeSource.Bangumi,
        };

        /// <summary>面板上标注的数据来源。</summary>
        public static string GetSourceText(TodayThemeKind kind) => GetSource(kind) switch
        {
            TodayThemeSource.Personal => "你的记录",
            TodayThemeSource.Mixed => "你的记录 · Bangumi",
            _ => "Bangumi",
        };

        /// <summary>
        /// 依赖你的标记、评分、浏览或播放记录。这些随时会变，内容不跨加载缓存，
        /// 回到页面时重新计算。
        /// </summary>
        public static bool UsesPersonalData(TodayThemeKind kind)
            => GetSource(kind) != TodayThemeSource.Bangumi;
    }

    /// <summary>
    /// 按日期决定当天的主题：9 天一轮，每轮顺序打乱；
    /// 一轮的第一个和上一轮的最后一个相同时交换前两个，同一主题不会连续两天出现。
    /// </summary>
    internal static class TodayThemeSchedule
    {
        // 轮换的起点，只影响每轮从哪天开始。
        private static readonly DateOnly Epoch = new(2026, 1, 5);
        // 与按日期打乱的键错开，避免两者的顺序互相关联。
        private const uint CycleSalt = 0x5EA50000;

        private static readonly TodayThemeKind[] All = Enum.GetValues<TodayThemeKind>();

        public static int Count => All.Length;

        /// <summary>当天的主题，以及它在本轮中的位置（从 0 开始）。</summary>
        public static (TodayThemeKind Kind, int Position) For(DateOnly date)
        {
            var days = date.DayNumber - Epoch.DayNumber;
            var cycle = days >= 0 ? days / All.Length : (days - All.Length + 1) / All.Length;
            var position = days - cycle * All.Length;
            return (CycleOrder(cycle)[position], position);
        }

        internal static IReadOnlyList<TodayThemeKind> CycleOrder(int cycle)
        {
            var order = RawOrder(cycle).ToArray();
            if (order[0] == RawOrder(cycle - 1)[^1])
                (order[0], order[1]) = (order[1], order[0]);

            return order;
        }

        private static IReadOnlyList<TodayThemeKind> RawOrder(int cycle)
            => StableShuffle.ByKey(All.Select(kind => (int)kind), unchecked((uint)cycle ^ CycleSalt))
                .Select(value => (TodayThemeKind)value)
                .ToList();
    }
}
