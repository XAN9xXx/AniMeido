using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using System.Text.Json;

namespace AniMeido.Plugin.Base.Services
{
    /// <summary>
    /// 本地搜索服务：搜索已标记和已缓存的番剧。
    /// 搜索范围包括标题和描述，不区分大小写。
    /// </summary>
    public class LocalSearchService
    {
        private readonly TrackingService _tracking;
        private readonly IAnimeDataSource _dataSource;
        private readonly CacheService _cacheService;
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };



        internal LocalSearchService(TrackingService tracking, IAnimeDataSource dataSource, CacheService cacheService)
        {
            _tracking = tracking;
            _dataSource = dataSource;
            _cacheService = cacheService;
        }


        /// <summary>
        /// 搜索已标记的番剧。匹配标题和描述，不区分大小写。
        /// </summary>
        /// <param name="query">搜索关键字</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>匹配的 Anime 列表（含状态信息）</returns>
        public async Task<List<SearchResult>> SearchTrackedAsync(string query, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new List<SearchResult>();

            query = query.Trim();
            var results = new List<SearchResult>();

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
            var trackedIds = allStatuses
                .SelectMany(status =>
                    idsByStatus.GetValueOrDefault(status) ?? [])
                .Distinct()
                .ToList();

            // 为每个 ID 获取详情并匹配
            foreach (var id in trackedIds)
            {
                if (ct.IsCancellationRequested) break;

                var anime = await TryGetAnimeAsync(id, ct);
                if (anime == null) continue;

                if (MatchesQuery(anime, query))
                {
                    var status = await _tracking.GetStatusAsync(id);
                    results.Add(new SearchResult(anime, status ?? AnimeTrackingStatus.None));
                }
            }

            return results;
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
            catch (TaskCanceledException)
            {
                return null;
            }
        }


        private static bool MatchesQuery(Anime anime, string query)
        {
            var lowerQuery = query.ToLowerInvariant();

            if (anime.Title.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase))
                return true;

            if (anime.Description?.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase) == true)
                return true;

            if (anime.Studio?.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase) == true)
                return true;

            return false;
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
