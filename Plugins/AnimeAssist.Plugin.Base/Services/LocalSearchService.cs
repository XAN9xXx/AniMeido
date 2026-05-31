using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.DependencyInjection;
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
                AnimeTrackingStatus.Blocked,
            };

            var trackedIds = new List<int>();
            foreach (var status in allStatuses)
            {
                var ids = await _tracking.GetAnimeIdsByStatusAsync(status);
                trackedIds.AddRange(ids);
            }

            trackedIds = trackedIds.Distinct().ToList();

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
            // 先尝试从缓存读取
            var cached = await _cacheService.GetCacheAsync($"detail:{animeId}");
            if (cached != null)
            {
                try
                {
                    // detail 缓存存的是 SubjectResponse，不是直接 Anime 对象
                    // 所以这里从 API 获取（已有缓存保护）
                }
                catch { }
            }

            // 通过 DataSource 获取（走缓存+网络）
            try
            {
                return await _dataSource.GetAnimeDetailAsync(animeId, ct);
            }
            catch
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

        public SearchResult(Anime anime, AnimeTrackingStatus status)
        {
            Anime = anime;
            TrackingStatus = status;
        }
    }
}
