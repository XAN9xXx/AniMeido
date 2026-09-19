using System.Collections.Concurrent;
using System.Globalization;
using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
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
        int? ExcludedAnimeId)
    {
        /// <summary>每部作品最近一次标记变化的时间；解析不了的没有。</summary>
        public IReadOnlyDictionary<int, DateTimeOffset> UpdatedAt { get; init; } =
            new Dictionary<int, DateTimeOffset>();
    }

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
    internal sealed class TodayThemeService(
        IAnimeDataSource dataSource,
        TrackingService tracking,
        BrowseHistoryService browseHistory,
        ArchiveService archive,
        ActionCenterService actionCenter)
    {
        // 高分类主题只在评分最高的这些作品里轮换，换一批时在其中翻页。
        internal const int PoolSize = 36;
        // 本地主题要逐部补作品详情（有缓存），候选少取一些。
        internal const int LocalPoolSize = 12;
        // “你看过好几次的”至少要有这么多部才显示，否则当天改用替补。
        internal const int MinRevisitedCount = 2;
        // 超过这么多天没有动静，算作搁置。
        internal const int StalledDays = 30;
        // “一年前的今天”取前后这么多天。
        internal const int OneYearAgoWindowDays = 7;

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

                case TodayThemeKind.RevisitedBrowse:
                    return await BuildRevisitedAsync(context, ct);

                case TodayThemeKind.UnratedThisSeason:
                    return await BuildUnratedAsync(context, ct);

                case TodayThemeKind.OneYearAgoToday:
                    return await BuildOneYearAgoTodayAsync(context, ct);

                case TodayThemeKind.Stalled:
                    return await BuildStalledAsync(context, ct);

                default:
                    // 其余主题分批实现；还没实现的按“没有内容”处理，当天改用替补。
                    return null;
            }
        }

        // ======== Bangumi 主题 ========

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

        // ======== 本地主题 ========

        private async Task<TodayThemeContent?> BuildRevisitedAsync(
            TodayThemeContext context,
            CancellationToken ct)
        {
            var history = await browseHistory.GetHistoryAsync(200, ct);
            var picked = SelectRevisited(history, context);
            if (picked.Count < MinRevisitedCount)
                return null;

            var anime = await ResolveAsync(picked.Select(item => (item.AnimeId, item.Title)).ToList(), ct);
            return new TodayThemeContent(
                "浏览过两次以上、还没有标记的作品 · 最近看过的在前",
                picked.Select(item => new TodayThemeCandidate(
                        anime[item.AnimeId],
                        BuildMeta(anime[item.AnimeId]),
                        $"看过 {item.ViewCount} 次 · 上次 {item.LastViewed.ToLocalTime():M月d日}"))
                    .ToList(),
                TodayThemeRowKind.Mark,
                ShuffleByDate: false);
        }

        /// <summary>浏览过两次以上、没有任何标记、不是今日一抽那部的作品，最近看过的在前。</summary>
        internal static IReadOnlyList<(int AnimeId, string? Title, DateTime LastViewed, int ViewCount)> SelectRevisited(
            IEnumerable<(int AnimeId, string? Title, DateTime LastViewed, int ViewCount)> history,
            TodayThemeContext context)
            => history
                .Where(item => item.ViewCount >= 2
                    && context.Statuses.GetValueOrDefault(item.AnimeId) == AnimeTrackingStatus.None
                    && item.AnimeId != context.ExcludedAnimeId)
                .DistinctBy(item => item.AnimeId)
                .OrderByDescending(item => item.LastViewed)
                .Take(LocalPoolSize)
                .ToList();

        private async Task<TodayThemeContent?> BuildUnratedAsync(
            TodayThemeContext context,
            CancellationToken ct)
        {
            var (year, season) = SeasonOf(context.Date);
            var seasonAnime = await dataSource.GetAnimeBySeasonAsync(year, season, ct);
            var rated = (await archive.GetArchiveListAsync(ct))
                .Where(item => item.Archive.PersonalRating is not null)
                .Select(item => item.Archive.AnimeId)
                .ToHashSet();
            var progress = await actionCenter.GetProgressAsync(ct);
            var picked = SelectUnrated(seasonAnime, context, rated);
            return picked.Count == 0
                ? null
                : new TodayThemeContent(
                    $"{year} 年{SeasonName(season)} · 你看完或正在追、还没打分的",
                    picked.Select(anime => new TodayThemeCandidate(
                            anime,
                            JoinParts(FormatText(anime.MediaFormat), DescribeProgress(anime.ID, context, progress))))
                        .ToList(),
                    TodayThemeRowKind.Rate,
                    ShuffleByDate: false);
        }

        /// <summary>本季作品里已看完或追番中、还没有个人评分的；看完的在前，各自按最近标记排列。</summary>
        internal static IReadOnlyList<Anime> SelectUnrated(
            IEnumerable<Anime> seasonAnime,
            TodayThemeContext context,
            IReadOnlySet<int> rated)
            => seasonAnime
                .DistinctBy(anime => anime.ID)
                .Where(anime => context.Statuses.GetValueOrDefault(anime.ID)
                        is AnimeTrackingStatus.Completed or AnimeTrackingStatus.Watching
                    && !rated.Contains(anime.ID))
                .OrderBy(anime => context.Statuses[anime.ID] == AnimeTrackingStatus.Completed ? 0 : 1)
                .ThenByDescending(anime => context.UpdatedAt.GetValueOrDefault(anime.ID))
                .Take(LocalPoolSize)
                .ToList();

        private async Task<TodayThemeContent?> BuildOneYearAgoTodayAsync(
            TodayThemeContext context,
            CancellationToken ct)
        {
            var events = await tracking.GetTrackingEventsAsync(ct);
            var picked = SelectOneYearAgo(events, context);
            if (picked.Count == 0)
                return null;

            var anime = await ResolveAsync(picked.Select(item => (item.AnimeId, (string?)null)).ToList(), ct);
            var center = context.Date.AddYears(-1);
            var from = center.AddDays(-OneYearAgoWindowDays);
            var to = center.AddDays(OneYearAgoWindowDays);
            return new TodayThemeContent(
                $"{from:yyyy 年 M 月 d 日} – {to:M 月 d 日} · 你当时的标记",
                picked.Select(item => new TodayThemeCandidate(
                        anime[item.AnimeId],
                        BuildMeta(anime[item.AnimeId]),
                        $"{item.ChangedAt.ToLocalTime():yyyy/M/d} · {DescribeEvent(item.NewStatus)}"))
                    .ToList(),
                TodayThemeRowKind.None,
                ShuffleByDate: false);
        }

        /// <summary>
        /// 一年前前后几天里变成追番、看完或补番的作品，每部取那几天里最近的一次；
        /// 现在已屏蔽的不列出。按当时的时间排列。
        /// </summary>
        internal static IReadOnlyList<(int AnimeId, AnimeTrackingStatus NewStatus, DateTimeOffset ChangedAt)> SelectOneYearAgo(
            IEnumerable<(int AnimeId, AnimeTrackingStatus NewStatus, DateTimeOffset ChangedAt)> events,
            TodayThemeContext context)
        {
            var center = context.Date.AddYears(-1);
            var from = center.AddDays(-OneYearAgoWindowDays);
            var to = center.AddDays(OneYearAgoWindowDays);
            return events
                .Where(item => item.NewStatus
                        is AnimeTrackingStatus.Watching
                        or AnimeTrackingStatus.Completed
                        or AnimeTrackingStatus.PlanToWatch
                    && DateOnly.FromDateTime(item.ChangedAt.ToLocalTime().DateTime) is var day
                    && day >= from
                    && day <= to
                    && context.Statuses.GetValueOrDefault(item.AnimeId) != AnimeTrackingStatus.Blocked)
                .GroupBy(item => item.AnimeId)
                .Select(group => group.MaxBy(item => item.ChangedAt))
                .OrderBy(item => item.ChangedAt)
                .Take(LocalPoolSize)
                .ToList();
        }

        private async Task<TodayThemeContent?> BuildStalledAsync(
            TodayThemeContext context,
            CancellationToken ct)
        {
            var progress = await actionCenter.GetProgressAsync(ct);
            // 补番计划里还没开始的已经列在右边，这里不重复。
            var pendingPlans = (await actionCenter.GetPlansAsync(cancellationToken: ct))
                .Select(plan => plan.AnimeId)
                .ToHashSet();
            var picked = SelectStalled(context, progress, pendingPlans, DateTimeOffset.Now);
            if (picked.Count == 0)
                return null;

            var anime = await ResolveAsync(picked.Select(item => (item.AnimeId, (string?)null)).ToList(), ct);
            return new TodayThemeContent(
                $"追番中或补番中、超过 {StalledDays} 天没有动静的 · 停得最久的在前",
                picked.Select(item => new TodayThemeCandidate(
                        anime[item.AnimeId],
                        JoinParts(FormatText(anime[item.AnimeId].MediaFormat), DescribeProgress(item.AnimeId, context, progress)),
                        $"停了 {item.Days} 天"))
                    .ToList(),
                TodayThemeRowKind.Finish,
                ShuffleByDate: false);
        }

        /// <summary>
        /// 追番中或补番中、最后一次标记变化与最后一次播放都在 <see cref="StalledDays"/> 天前的作品；
        /// 两个时间都不知道的跳过。停得最久的在前。
        /// </summary>
        internal static IReadOnlyList<(int AnimeId, int Days)> SelectStalled(
            TodayThemeContext context,
            IReadOnlyDictionary<int, AnimeProgressSnapshot> progress,
            IReadOnlySet<int> pendingPlans,
            DateTimeOffset now)
            => context.Statuses
                .Where(pair => pair.Value is AnimeTrackingStatus.Watching or AnimeTrackingStatus.PlanToWatch
                    && !pendingPlans.Contains(pair.Key))
                .Select(pair => (AnimeId: pair.Key, Last: LastActivity(pair.Key, context, progress)))
                .Where(item => item.Last is not null)
                .Select(item => (item.AnimeId, Days: (int)(now - item.Last!.Value).TotalDays))
                .Where(item => item.Days >= StalledDays)
                .OrderByDescending(item => item.Days)
                .ThenBy(item => item.AnimeId)
                .Take(LocalPoolSize)
                .ToList();

        private static DateTimeOffset? LastActivity(
            int animeId,
            TodayThemeContext context,
            IReadOnlyDictionary<int, AnimeProgressSnapshot> progress)
        {
            DateTimeOffset? marked = context.UpdatedAt.TryGetValue(animeId, out var updatedAt) ? updatedAt : null;
            DateTimeOffset? watched = progress.TryGetValue(animeId, out var item) ? item.LastWatchedAt : null;
            return marked is null ? watched
                : watched is null ? marked
                : marked > watched ? marked : watched;
        }

        // ======== 共用 ========

        /// <summary>逐部补作品详情（有缓存）；单部失败时用标题占位，不影响其他作品。</summary>
        private async Task<IReadOnlyDictionary<int, Anime>> ResolveAsync(
            IReadOnlyList<(int AnimeId, string? Title)> items,
            CancellationToken ct)
        {
            var result = new ConcurrentDictionary<int, Anime>();
            await Parallel.ForEachAsync(
                items,
                new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 4 },
                async (item, token) =>
                {
                    Anime? anime = null;
                    try
                    {
                        anime = await dataSource.GetAnimeDetailAsync(item.AnimeId, token);
                    }
                    catch (Exception ex) when (
                        ex is HttpRequestException
                        or BangumiApiException
                        or InvalidOperationException
                        or System.Text.Json.JsonException
                        or TaskCanceledException)
                    {
                        if (ex is OperationCanceledException && token.IsCancellationRequested)
                            throw;
                    }

                    result[item.AnimeId] = anime ?? new Anime(
                        item.AnimeId,
                        item.Title ?? $"Bangumi #{item.AnimeId}",
                        null,
                        [],
                        null,
                        null,
                        string.Empty,
                        0,
                        0);
                });
            return result;
        }

        private static string DescribeProgress(
            int animeId,
            TodayThemeContext context,
            IReadOnlyDictionary<int, AnimeProgressSnapshot> progress)
        {
            var status = context.Statuses.GetValueOrDefault(animeId);
            if (status == AnimeTrackingStatus.Completed)
            {
                return context.UpdatedAt.TryGetValue(animeId, out var finished)
                    ? $"看完于 {finished.ToLocalTime():M月d日}"
                    : "已看完";
            }

            return progress.TryGetValue(animeId, out var item) && item.CurrentEpisode > 0
                ? $"看到第 {item.CurrentEpisode} 集"
                : "";
        }

        private static string DescribeEvent(AnimeTrackingStatus status) => status switch
        {
            AnimeTrackingStatus.Watching => "开始追番",
            AnimeTrackingStatus.Completed => "看完",
            _ => "加进补番计划",
        };

        internal static (int Year, Season Season) SeasonOf(DateOnly date)
            => (date.Year, SeasonHelper.FromMonth(date.Month));

        internal static (int Year, Season Season) PreviousSeason((int Year, Season Season) current)
            => current.Season == Season.Winter
                ? (current.Year - 1, Season.Fall)
                : (current.Year, current.Season - 1);

        /// <summary>形态与年份，例如“TV · 2016”“剧场版 · 2002”。</summary>
        internal static string BuildMeta(Anime anime)
        {
            var year = anime.AirDate?.Year ?? (anime.SeasonYear > 0 ? anime.SeasonYear : (int?)null);
            return JoinParts(
                FormatText(anime.MediaFormat),
                year?.ToString(CultureInfo.InvariantCulture) ?? "");
        }

        private static string FormatText(AnimeMediaFormat format) => format switch
        {
            AnimeMediaFormat.Movie => "剧场版",
            AnimeMediaFormat.Ova => "OVA",
            AnimeMediaFormat.Ona => "网络",
            AnimeMediaFormat.Television => "TV",
            _ => "",
        };

        private static string JoinParts(params string[] parts)
            => string.Join(" · ", parts.Where(part => part.Length > 0));

        internal static string SeasonName(Season season) => season switch
        {
            Season.Winter => "冬季",
            Season.Spring => "春季",
            Season.Summer => "夏季",
            _ => "秋季",
        };

        /// <summary>解析标记时间；格式不一（可能来自导入），解析不了返回 null。</summary>
        internal static DateTimeOffset? ParseTimestamp(string value)
            => DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed)
                ? parsed
                : null;
    }
}
