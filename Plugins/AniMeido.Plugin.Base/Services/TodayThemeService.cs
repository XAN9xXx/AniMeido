using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;

namespace AniMeido.Plugin.Base.Services
{
    /// <summary>主题行上的操作。</summary>
    public enum TodayThemeRowKind
    {
        /// <summary>只能打开详情。</summary>
        None,
        /// <summary>补番 · 关注（可再点一次取消）。</summary>
        Mark,
        /// <summary>评分：打开档案馆里这部作品。</summary>
        Rate,
        /// <summary>看完了 · 弃番。</summary>
        Finish,
    }

    /// <summary>取候选时需要的本地信息。</summary>
    /// <param name="Date">当天。</param>
    /// <param name="Statuses">全部本地标记。</param>
    /// <param name="ExcludedAnimeId">放送日历今日一抽当天抽中的作品，不再列出。</param>
    internal sealed record TodayThemeContext(
        DateOnly Date,
        IReadOnlyDictionary<int, AnimeTrackingStatus> Statuses,
        int? ExcludedAnimeId);

    /// <summary>主题里的一部作品。</summary>
    /// <param name="Anime">作品。</param>
    /// <param name="Meta">形态、年份等。</param>
    /// <param name="Note">这部作品为什么出现，例如“停了 41 天”；可为空。</param>
    internal sealed record TodayThemeCandidate(
        Anime Anime,
        string Meta,
        string Note = "");

    /// <summary>一个主题的全部候选。</summary>
    /// <param name="Caption">标题下方说明这批作品是怎么来的。</param>
    /// <param name="Candidates">候选，换一批时在其中翻页。</param>
    /// <param name="RowKind">行上的操作。</param>
    /// <param name="ShuffleByDate">按日期打乱候选顺序；为 false 时保持给出的顺序。</param>
    internal sealed record TodayThemeContent(
        string Caption,
        IReadOnlyList<TodayThemeCandidate> Candidates,
        TodayThemeRowKind RowKind,
        bool ShuffleByDate = true);

    /// <summary>
    /// 为今天页“今日主题”取候选。返回 null 表示这个主题今天没有足够的内容，由调用方改用替补；
    /// 网络或解析失败直接抛出，由调用方显示重试。
    /// </summary>
    internal sealed class TodayThemeService(IAnimeDataSource dataSource)
    {
        // 高分类主题只在评分最高的这些作品里轮换，换一批时在其中翻页。
        internal const int PoolSize = 36;

        public async Task<TodayThemeContent?> BuildAsync(
            TodayThemeKind kind,
            TodayThemeContext context,
            CancellationToken ct)
        {
            switch (kind)
            {
                case TodayThemeKind.LastSeasonTop:
                {
                    var (year, season) = PreviousSeason(SeasonOf(context.Date));
                    return await BuildSeasonAsync(year, season, context, ct);
                }

                case TodayThemeKind.OneYearAgoSeason:
                {
                    var (year, season) = SeasonOf(context.Date);
                    return await BuildSeasonAsync(year - 1, season, context, ct);
                }

                case TodayThemeKind.Movies:
                    return await BuildMoviesAsync(context, ct);

                default:
                    // 其余主题分批实现；还没实现的按“没有内容”处理，当天改用替补。
                    return null;
            }
        }

        private async Task<TodayThemeContent?> BuildSeasonAsync(
            int year,
            Season season,
            TodayThemeContext context,
            CancellationToken ct)
        {
            var items = await dataSource.GetAnimeBySeasonAsync(year, season, ct);
            var ranked = RankUnmarked(items, context);
            return ranked.Count == 0
                ? null
                : new TodayThemeContent(
                    $"{year} 年{SeasonName(season)} · 你还没标记的 · 评分来自 Bangumi",
                    ranked.Select(anime => new TodayThemeCandidate(anime, BuildMeta(anime))).ToList(),
                    TodayThemeRowKind.Mark);
        }

        private async Task<TodayThemeContent?> BuildMoviesAsync(
            TodayThemeContext context,
            CancellationToken ct)
        {
            var (items, _) = await dataSource.SearchByTagAsync("剧场版", 0, "rank", ct);
            // 搜索结果按标签匹配，形态未知的也保留；明确是其他形态的去掉。
            var movies = items.Where(anime => anime.MediaFormat
                is AnimeMediaFormat.Movie
                or AnimeMediaFormat.Unknown);
            var ranked = RankUnmarked(movies, context);
            return ranked.Count == 0
                ? null
                : new TodayThemeContent(
                    "Bangumi 评分较高 · 你还没标记的剧场版",
                    ranked.Select(anime => new TodayThemeCandidate(anime, BuildMeta(anime))).ToList(),
                    TodayThemeRowKind.Mark);
        }

        /// <summary>有评分、没有任何标记（含屏蔽）、不是今日一抽那部的作品，按评分从高到低取前若干部。</summary>
        internal static IReadOnlyList<Anime> RankUnmarked(
            IEnumerable<Anime> items,
            TodayThemeContext context)
            => items
                .DistinctBy(anime => anime.ID)
                .Where(anime => anime.Score is > 0
                    && context.Statuses.GetValueOrDefault(anime.ID) == AnimeTrackingStatus.None
                    && anime.ID != context.ExcludedAnimeId)
                .OrderByDescending(anime => anime.Score)
                .ThenBy(anime => anime.ID)
                .Take(PoolSize)
                .ToList();

        internal static (int Year, Season Season) SeasonOf(DateOnly date)
            => (date.Year, SeasonHelper.FromMonth(date.Month));

        internal static (int Year, Season Season) PreviousSeason((int Year, Season Season) current)
            => current.Season == Season.Winter
                ? (current.Year - 1, Season.Fall)
                : (current.Year, current.Season - 1);

        /// <summary>形态与年份，例如“TV · 2016”“剧场版 · 2002”。</summary>
        internal static string BuildMeta(Anime anime)
        {
            var format = anime.MediaFormat switch
            {
                AnimeMediaFormat.Movie => "剧场版",
                AnimeMediaFormat.Ova => "OVA",
                AnimeMediaFormat.Ona => "网络",
                AnimeMediaFormat.Television => "TV",
                _ => "",
            };
            var year = anime.AirDate?.Year ?? (anime.SeasonYear > 0 ? anime.SeasonYear : (int?)null);
            return string.Join(" · ", new[] { format, year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "" }
                .Where(part => part.Length > 0));
        }

        internal static string SeasonName(Season season) => season switch
        {
            Season.Winter => "冬季",
            Season.Spring => "春季",
            Season.Summer => "夏季",
            _ => "秋季",
        };
    }
}
