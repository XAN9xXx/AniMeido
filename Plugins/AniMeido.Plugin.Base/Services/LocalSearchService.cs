using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
using System.Collections.Concurrent;

namespace AniMeido.Plugin.Base.Services
{
    /// <summary>
    /// 本地搜索服务：搜索已标记和已缓存的番剧。
    /// 搜索范围包括标题、别名和制作公司，不区分大小写。
    /// </summary>
    public class LocalSearchService
    {
        private readonly TrackingService _tracking;
        private readonly IAnimeDataSource _dataSource;
        internal LocalSearchService(
            TrackingService tracking,
            IAnimeDataSource dataSource)
        {
            _tracking = tracking;
            _dataSource = dataSource;
        }


        /// <summary>
        /// 搜索已标记的番剧（不含屏蔽）。匹配标题、别名和制作公司，不区分大小写。
        /// </summary>
        /// <param name="query">搜索关键字</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>匹配的 Anime 列表（含状态信息）</returns>
        public async Task<List<SearchResult>> SearchTrackedAsync(string query, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new List<SearchResult>();

            query = query.Trim();
            // 收集所有已标记的 ID
            var allStatuses = new[]
            {
                AnimeTrackingStatus.Watching,
                AnimeTrackingStatus.PlanToWatch,
                AnimeTrackingStatus.NotInterested,
                AnimeTrackingStatus.Following,
                AnimeTrackingStatus.Completed,
                AnimeTrackingStatus.Dropped,
            };

            var idsByStatus =
                await _tracking.GetAnimeIdsGroupedByStatusAsync();
            var statusById = allStatuses
                .SelectMany(status =>
                    (idsByStatus.GetValueOrDefault(status) ?? [])
                        .Select(id => (id, status)))
                .GroupBy(item => item.id)
                .ToDictionary(
                    group => group.Key,
                    group => group.First().status);
            var trackedIds = allStatuses
                .SelectMany(status =>
                    idsByStatus.GetValueOrDefault(status) ?? [])
                .Distinct()
                .ToList();

            var matches = new ConcurrentBag<(int Index, SearchResult Result)>();
            await Parallel.ForEachAsync(
                trackedIds.Select((id, index) => (id, index)),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = 4,
                    CancellationToken = ct,
                },
                async (item, cancellationToken) =>
                {
                    var anime = await TryGetAnimeAsync(
                        item.id,
                        cancellationToken);
                    if (anime is not null && MatchesQuery(anime, query))
                    {
                        matches.Add((
                            item.index,
                            new SearchResult(
                                anime,
                                statusById.GetValueOrDefault(item.id))));
                    }
                });

            return matches
                .OrderBy(item => item.Index)
                .Select(item => item.Result)
                .ToList();
        }


        /// <summary>
        /// 尝试从缓存获取 Anime，缓存未命中则从 API 获取。
        /// </summary>
        private async Task<Anime?> TryGetAnimeAsync(int animeId, CancellationToken ct)
        {
            // 本地搜索不直接从 detail 缓存反序列化（缓存格式为 SubjectResponse），
            // 由 IAnimeDataSource.GetAnimeDetailAsync 内部处理缓存。
            try
            {
                return await _dataSource.GetAnimeDetailAsync(animeId, ct);
            }
            catch (HttpRequestException)
            {
                return null;
            }
            catch (BangumiApiException)
            {
                return null;
            }
            catch (OperationCanceledException)
                when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException)
            {
                return null;
            }
        }


        /// <summary>
        /// 匹配标题、别名（原名、外文名）与制作公司。不匹配简介：常见词在简介里出现得太多，
        /// 会把大半收藏都搜出来。
        /// </summary>
        internal static bool MatchesQuery(Anime anime, string query)
        {
            if (anime.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                return true;

            if (anime.AlternateTitles?.Any(title => title.Contains(query, StringComparison.OrdinalIgnoreCase)) == true)
                return true;

            return anime.Studio?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
        }
    }


    /// <summary>
    /// 搜索结果，包含 Anime 及其当前标记状态。
    /// </summary>
    public class SearchResult
    {
        public Anime Anime { get; }
        public AnimeTrackingStatus TrackingStatus { get; }

        public string StatusLabel => TrackingStatus switch
        {
            AnimeTrackingStatus.Watching => "追番中",
            AnimeTrackingStatus.PlanToWatch => "补番中",
            AnimeTrackingStatus.NotInterested => "不感兴趣",
            AnimeTrackingStatus.Following => "关注",
            AnimeTrackingStatus.Completed => "已看完",
            AnimeTrackingStatus.Dropped => "已弃番",
            AnimeTrackingStatus.Blocked => "已屏蔽",
            _ => ""
        };

        public SearchResult(Anime anime, AnimeTrackingStatus status)
        {
            Anime = anime;
            TrackingStatus = status;
        }
    }
}
