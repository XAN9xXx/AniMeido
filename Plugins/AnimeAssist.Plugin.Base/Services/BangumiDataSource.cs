using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Models.Bangumi;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace AniMeido.Plugin.Base.Services
{
    /// <summary>
    /// 用于从Bangumi API获取番剧数据的服务类，实现了IAnimeDataSource接口。
    /// </summary>
    public sealed class BangumiDataSource : Contracts.IAnimeDataSource
    {
        private readonly BangumiApiClient _apiClient;
        private readonly ILogger<BangumiDataSource> _logger;
        private readonly CacheService _cacheService;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _cacheMissGates = new();
        private const string FallbackTitle = "不好，标题走丢了Q^Q";
        private const string FallbackDescription = "No description available.";
        private const int SeasonSearchPageSize = 20;
        private const int SeasonCacheVersion = 3;
        private const int BroadcastCacheVersion = 1;
        private static readonly string? FallbackImageUrl = null;
        private static readonly IReadOnlyList<VoiceActor> FallbackCVs = Array.Empty<VoiceActor>();
        private static readonly IReadOnlyList<string> StudioFilter = new List<string> { "製作", "原作", "企画", "动画制作", "发行" }; // API中Type 2 代表参与制作的商业实体，此处仅筛选制作/原作。

        /// <summary>判断异常是否为网络错误（含 BangumiApiException 包装的网络异常）。</summary>
        private static bool IsNetworkError(Exception ex) => ex switch
        {
            HttpRequestException => true,
            TaskCanceledException => true,
            BangumiApiException bae => bae.InnerException switch
            {
                HttpRequestException => true,
                TaskCanceledException => true,
                _ => false
            },
            _ => false
        };



        internal BangumiDataSource(ILogger<BangumiDataSource> logger, BangumiApiClient apiClient, CacheService cacheService)
        {
            ArgumentNullException.ThrowIfNull(logger, nameof(logger));
            ArgumentNullException.ThrowIfNull(apiClient, nameof(apiClient));
            ArgumentNullException.ThrowIfNull(cacheService, nameof(cacheService));
            _logger = logger;
            _apiClient = apiClient;
            _cacheService = cacheService;
        }



        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };

        // 以年-季度为条件从API筛选番剧，返回Anime列表的辅助方法
        private async Task<List<Anime>> FetchByBrowse(int year, int seasonMonth, CancellationToken ct)
        {
            var result = await _apiClient.GetJsonAsync<PagedSubjectResponse>($"/v0/subjects?type=2&year={year}&month={seasonMonth}&limit=50", ct);
            List<Anime> animes = new List<Anime>();
            if (result is null)
            {
                _logger.LogError("Bangumi calendar API returned null.");
                throw new BangumiApiException("Bangumi calendar API returned null.");
            }

            foreach (var item in result.Data)
            {
                var anime = MapFromSubject(item, year, seasonMonth);
                animes.Add(anime);
            }

            return animes;
        }

        // 从Bangumi API获取每周新番的日历数据，并将其解析为CalendarDayResponse对象列表。
        private async Task<List<CalendarDayResponse>> FetchCalendarAsync(CancellationToken ct)
        {
            var result = await _apiClient.GetJsonAsync<List<CalendarDayResponse>>("/calendar", ct).ConfigureAwait(false);
            if (result is null)
            {
                _logger.LogError("Bangumi calendar API returned null.");
                throw new BangumiApiException("Bangumi calendar API returned null.");
            }
            return result;
        }

        private async Task<T?> GetCacheAsync<T>(string cacheKey, TimeSpan expiration, Func<Task<T?>> fetchFunc) where T : class
        {
            // 尝试从缓存读取
            var cached = await _cacheService.GetCacheAsync(cacheKey);
            if (cached != null)
            {
                try
                {
                    var result = JsonSerializer.Deserialize<T>(cached, JsonOptions);
                    if (result != null) return result;
                }
                catch (JsonException ex)
                {
                    // 缓存数据损坏，删除坏缓存
                    _logger.LogWarning(ex, "Corrupted cache entry for {Key}, removing", cacheKey);
                    await _cacheService.RemoveCacheAsync(cacheKey);
                }
            }

            var cacheMissGate = _cacheMissGates.GetOrAdd(
                cacheKey,
                _ => new SemaphoreSlim(1, 1));
            await cacheMissGate.WaitAsync();
            try
            {
                // 另一个调用可能已在等待期间填充缓存。
                cached = await _cacheService.GetCacheAsync(cacheKey);
                if (cached is not null)
                {
                    try
                    {
                        var result = JsonSerializer.Deserialize<T>(cached, JsonOptions);
                        if (result is not null)
                            return result;
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "Corrupted cache entry for {Key}, removing", cacheKey);
                        await _cacheService.RemoveCacheAsync(cacheKey);
                    }
                }

                T? data;
                try
                {
                    data = await fetchFunc();
                }
                catch (Exception ex) when (IsNetworkError(ex))
                {
                    // 网络失败时尝试返回过期缓存降级
                    _logger.LogWarning("Network request failed for cache key {Key}: {Msg}", cacheKey, ex.Message);
                    var stale = await _cacheService.GetCacheAllowExpiredAsync(cacheKey);
                    if (stale != null)
                    {
                        try
                        {
                            var staleResult = JsonSerializer.Deserialize<T>(stale, JsonOptions);
                            if (staleResult != null)
                            {
                                _logger.LogInformation("Returning stale cache for {Key}", cacheKey);
                                return staleResult;
                            }
                        }
                        catch (JsonException jex)
                        {
                            _logger.LogWarning(jex, "Stale cache corrupted for {Key}, falling back to network error", cacheKey);
                        }
                    }
                    throw; // 没有过期缓存可用，继续保持原异常向上传播
                }

                if (data != null)
                {
                    var json = JsonSerializer.Serialize(data, JsonOptions);
                    await _cacheService.SetCacheAsync(cacheKey, json, expiration);
                }
                return data;
            }
            finally
            {
                cacheMissGate.Release();
            }
        }



        // 将CalendarItem对象映射为Anime对象，使用默认值处理缺失的信息，并记录无法解析的放送日期的警告日志
        private Anime MapToAnime(CalendarItem item, int year, int seasonMonth)
        {
            DateOnly? parsedDate = DateTime.TryParse(item.AirDate, out var dt) ? DateOnly.FromDateTime(dt) : null;

            if (parsedDate is null)
            {
                _logger.LogWarning("Failed to parse air date for item {ItemId}: {AirDate}", item.Id, item.AirDate);
            }
            return new Anime
            (
                item.Id,
                ResolveTitle(item.NameCn, item.Name),
                null,
                FallbackCVs,
                parsedDate,
                ResolveImageUrl(item.Images?.Large) ?? FallbackImageUrl,
                item.Summary ?? FallbackDescription,
                year,
                seasonMonth,
                item.AirWeekday,
                AlternateTitles: GetAlternateTitles(
                    item.NameCn,
                    item.Name)
                )
            { Score = item.Rating?.Score };
        }

        // 将ActorInfo映射为一个VoiceActor
        private static VoiceActor MapToVoiceActor(ActorInfo actor)
        {
            return new VoiceActor(actor.Id, actor.Name, ResolveImageUrl(actor.Image?.Grid) ?? FallbackImageUrl);
        }

        // 将ActorInfo列表映射为VoiceActor列表，处理可能的null值并返回空列表作为默认值。
        private static IReadOnlyList<VoiceActor> MapToVoiceActor(List<ActorInfo>? actors)
        {
            return actors?.Select(a => MapToVoiceActor(a)).ToList() ?? [];
        }

        // 将RelatedCharacterResponse对象映射为CharacterRole对象，提取角色信息和配音演员列表，并处理可能的null值。
        private static CharacterRole MapToCharacterRole(RelatedCharacterResponse character)
        {
            var actors = MapToVoiceActor(character.Actors);
            var image = ResolveImageUrl(character.Images?.Grid) ?? FallbackImageUrl;
            return new CharacterRole(character.Id, character.Name, character.Summary, image, actors);
        }



        // 将 Bangumi 图片 URL 替换为反代地址
        private static string? ResolveImageUrl(string? rawUrl)
        {
            if (string.IsNullOrEmpty(rawUrl)) return null;

            // 处理 protocol-relative URL (//host/path)
            var url = rawUrl.AsSpan().TrimStart();
            if (url.StartsWith("//".AsSpan()))
                url = $"https:{rawUrl}".AsSpan();

            if (!Uri.TryCreate(url.ToString(), UriKind.Absolute, out var parsed))
                return null;

            // http → https
            var builder = new UriBuilder(parsed) { Scheme = Uri.UriSchemeHttps, Port = -1 };

            // 替换 Bangumi 源站为反代
            if (builder.Host.Equals("lain.bgm.tv", StringComparison.OrdinalIgnoreCase))
                builder.Host = "bgm-proxy.animeido.com";

            return builder.Uri.ToString();
        }

        // 优先使用中文译名，其次日文原名，最后后备文字；同时处理 API 返回空字符串的情况
        private static string ResolveTitle(string? nameCn, string? name)
            => !string.IsNullOrWhiteSpace(nameCn) ? nameCn
             : !string.IsNullOrWhiteSpace(name) ? name
             : FallbackTitle;

        private static IReadOnlyList<string> GetAlternateTitles(
            string? nameCn,
            string? name)
        {
            var primary = ResolveTitle(nameCn, name);
            return new[] { nameCn, name }
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .Select(title => title!.Trim())
                .Where(title => !string.Equals(
                    title,
                    primary,
                    StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        // 判断条目的放送日期是否属于指定的年份和季度
        private static bool BelongsToSeason(CalendarItem item, int year, Season season)
        {
            if (!DateTime.TryParse(item.AirDate, out var dt))
            {
                return false;
            }
            return dt.Year == year && SeasonHelper.FromMonth(dt.Month) == season;
        }

        // 根据角色定位返回优先级，主角优先，配角次之，闲角最后。
        private static int GetCharacterPriority(string? relation) => relation switch
        {
            "主角" => 1,
            "配角" => 2,
            "闲角" => 3,
            _ => 3
        };

        // 将DTO SubjectResponse映射为Anime的辅助方法
        private static Anime MapFromSubject(SubjectResponse item, int? year = null, int? seasonMonth = null)
        {
            DateOnly? parsedDate = DateTime.TryParse(item.Date, out var dt) ? DateOnly.FromDateTime(dt) : null;
            int resolvedYear = year ?? parsedDate?.Year ?? 0;
            int resolvedSeasonMonth = seasonMonth
                ?? (parsedDate.HasValue ? SeasonHelper.ToMonth(SeasonHelper.FromMonth(parsedDate.Value.Month)) : 0);

            return new Anime(
                item.Id,
                ResolveTitle(item.NameCn, item.Name),
                null,
                FallbackCVs,
                parsedDate,
                ResolveImageUrl(item.Images?.Large) ?? FallbackImageUrl,
                item.Summary ?? FallbackDescription,
                resolvedYear,
                resolvedSeasonMonth,
                AlternateTitles: GetAlternateTitles(
                    item.NameCn,
                    item.Name),
                MediaFormat: MapMediaFormat(item.Platform)
                )
            { Score = item.Rating?.Score };
        }

        internal static AnimeMediaFormat MapMediaFormat(string? platform)
        {
            if (string.IsNullOrWhiteSpace(platform))
            {
                return AnimeMediaFormat.Unknown;
            }

            return platform.Trim().ToUpperInvariant() switch
            {
                "TV" => AnimeMediaFormat.Television,
                "OVA" => AnimeMediaFormat.Ova,
                "MOVIE" or "剧场版" or "劇場版" => AnimeMediaFormat.Movie,
                "WEB" or "ONA" => AnimeMediaFormat.Ona,
                _ => AnimeMediaFormat.Unknown,
            };
        }



        /// <summary>
        /// 以年-季度为条件筛选番剧，调用FetchByBrowse辅助方法
        /// </summary>
        /// <param name="year">筛选条件-年</param>
        /// <param name="season">筛选条件-季度</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>Anime列表</returns>
        /// <exception cref="ArgumentOutOfRangeException">筛选条件超出范围</exception>
        public async Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct)
        {
            if (!Enum.IsDefined(season))
                throw new ArgumentOutOfRangeException(nameof(season));

            // 缓存版本属于查询语义的一部分。v1 的往季结果最多只有 40 条，
            // 使用新键可避免修复后继续命中已截断的旧缓存。
            var cacheKey = $"season:v{SeasonCacheVersion}:{year}:{season}";
            var cachedResult = await ReadSeasonCacheAsync(
                cacheKey,
                allowExpired: false);
            if (cachedResult is not null)
            {
                return cachedResult;
            }

            try
            {
                var (airDateFrom, airDateTo) = GetSeasonDateRange(year, season);
                var searchResult = await SearchBySeasonAsync(year, season, airDateFrom, airDateTo, ct);
                if (searchResult.Count > 0)
                {
                    await _cacheService.SetCacheAsync(cacheKey, JsonSerializer.Serialize(searchResult, JsonOptions), TimeSpan.FromHours(12));
                    return searchResult;
                }

                return [];
            }
#pragma warning disable CA1031 // 网络异常应使用 stale cache 降级
            catch (Exception ex) when (IsNetworkError(ex))
            {
                // 网络失败时尝试返回过期缓存降级
                _logger.LogWarning("Network request failed for season {Year}/{Season}: {Msg}", year, season, ex.Message);
                var staleResult = await ReadSeasonCacheAsync(
                    cacheKey,
                    allowExpired: true);
#pragma warning restore CA1031
                if (staleResult is not null)
                {
                    _logger.LogInformation(
                        "Returning stale cache for season {Year}/{Season}",
                        year,
                        season);
                    return staleResult;
                }
                throw;
            }
        }

        public async Task<List<Anime>> GetCurrentBroadcastScheduleAsync(
            CancellationToken ct)
        {
            var (year, season) = SeasonHelper.GetCurrentSeason();
            var cacheKey = $"broadcast:v{BroadcastCacheVersion}:{year}:{season}";
            return await GetCacheAsync(
                cacheKey,
                TimeSpan.FromHours(12),
                async () =>
                {
                    var seasonMonth = SeasonHelper.ToMonth(season);
                    var days = await FetchCalendarAsync(ct)
                        .ConfigureAwait(false);
                    return days
                        .SelectMany(day => day.Items)
                        .Where(item => BelongsToSeason(
                            item,
                            year,
                            season))
                        .Select(item => MapToAnime(
                            item,
                            year,
                            seasonMonth))
                        .DistinctBy(item => item.ID)
                        .ToList();
                }) ?? [];
        }

        private async Task<List<Anime>?> ReadSeasonCacheAsync(
            string cacheKey,
            bool allowExpired)
        {
            var cached = allowExpired
                ? await _cacheService.GetCacheAllowExpiredAsync(cacheKey)
                : await _cacheService.GetCacheAsync(cacheKey);
            if (cached is null)
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<List<Anime>>(
                    cached,
                    JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Removing invalid season cache {CacheKey}.",
                    cacheKey);
                await _cacheService.RemoveCacheAsync(cacheKey);
                return null;
            }
        }

        /// <summary>计算指定季度的放送日期范围。</summary>
        private static (string from, string to) GetSeasonDateRange(int year, Season season)
        {
            return season switch
            {
                Season.Winter => ($"{year}-01-01", $"{year}-04-01"),
                Season.Spring => ($"{year}-04-01", $"{year}-07-01"),
                Season.Summer => ($"{year}-07-01", $"{year}-10-01"),
                Season.Fall => ($"{year}-10-01", $"{(year + 1)}-01-01"),
                _ => ($"{year}-01-01", $"{(year + 1)}-01-01"),
            };
        }

        /// <summary>
        /// 使用 Bangumi 搜索 API 按放送日期范围查询季度番剧。
        /// 相比 /v0/subjects 的 year/month 参数，搜索 API 的 AirDate 筛选更精确。
        /// </summary>
        private async Task<List<Anime>> SearchBySeasonAsync(int year, Season season, string airDateFrom, string airDateTo, CancellationToken ct)
        {
            var filter = new SearchFilter(
                Type: new List<int> { 2 },
                AirDate: new List<string> { $">={airDateFrom}", $"<{airDateTo}" }
            );

            var request = new SearchSubjectRequest(
                Sort: "rank",
                Filter: filter
            );

            var allAnimes = new List<Anime>();
            var seenIds = new HashSet<int>();
            var offset = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var url = $"/v0/search/subjects?limit={SeasonSearchPageSize}&offset={offset}";
                var result = await _apiClient.PostJsonAsync<PagedSubjectResponse>(url, request, ct)
                    .ConfigureAwait(false);

                if (result is null)
                {
                    throw new BangumiApiException(
                        $"Bangumi season search returned null at offset {offset}.");
                }

                if (result.Data.Count == 0)
                {
                    if (offset >= result.Total)
                    {
                        break;
                    }

                    throw new BangumiApiException(
                        $"Bangumi season search ended at offset {offset} before reaching total {result.Total}.");
                }

                foreach (var item in result.Data)
                {
                    if (seenIds.Add(item.Id))
                    {
                        allAnimes.Add(MapFromSubject(item));
                    }
                }

                offset += result.Data.Count;
                if (offset >= result.Total || result.Data.Count < SeasonSearchPageSize)
                {
                    break;
                }
            }

            return allAnimes;
        }

        /// <summary>
        /// 获取特定番剧的详细信息
        /// </summary>
        /// <param name="animeID">番剧的唯一标识符</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>番剧详情信息</returns>
        public async Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct)
        {
            return await GetCacheAsync($"detail:{animeID}", TimeSpan.FromDays(7),
                async () =>
                {
                    var result = await _apiClient.GetJsonAsync<SubjectResponse>($"/v0/subjects/{animeID}", ct).ConfigureAwait(false);
                    return result != null ? MapFromSubject(result) : null;
                });
        }

        /// <summary>
        /// 获取特定番剧的Studio信息
        /// </summary>
        /// <param name="animeID">番剧的唯一标识符</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>Studio列表</returns>
        public async Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct)
        {
            return await GetCacheAsync($"studio:{animeID}", TimeSpan.FromDays(7),
                async () =>
                {
                    var result = await _apiClient.GetJsonAsync<List<RelatedPersonResponse>>($"/v0/subjects/{animeID}/persons", ct).ConfigureAwait(false);
                    if (result is null) return new List<Studio>();
                    return result.Where(person => person.Type == 2 && StudioFilter.Contains(person.Relation))
                        .Select(person => new Studio(person.Id, person.Name, ResolveImageUrl(person.Images?.Grid)))
                        .ToList();
                }) ?? [];
        }

        /// <summary>
        /// 获取特定番剧的Tag信息
        /// </summary>
        /// <param name="animeID">番剧的唯一标识符</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>Tag列表</returns>
        public async Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct)
        {
            return await GetCacheAsync($"tags:{animeID}", TimeSpan.FromDays(7),
                async () =>
                {
                    var result = await _apiClient.GetJsonAsync<SubjectResponse>($"/v0/subjects/{animeID}", ct).ConfigureAwait(false);
                    if (result is null) return new List<Tag>();
                    return result.MetaTags?.Select(tag => new Tag(tag)).ToList() ?? [];
                }) ?? [];
        }

        /// <summary>
        /// 获取特定番剧的CV信息
        /// </summary>
        /// <param name="animeID">番剧的唯一标识符</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>VoiceActor列表</returns>
        public async Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct)
        {
            return await GetCacheAsync($"cvs:{animeID}", TimeSpan.FromDays(7),
                async () =>
                {
                    var result = await _apiClient.GetJsonAsync<List<RelatedCharacterResponse>>($"/v0/subjects/{animeID}/characters", ct).ConfigureAwait(false);
                    if (result is null) return new List<VoiceActor>();
                    var sorted = result.OrderBy(c => GetCharacterPriority(c.Relation)).ToList();
                    return sorted.Select(character => MapToVoiceActor(character.Actors))
                        .SelectMany(cvs => cvs)
                        .DistinctBy(v => v.VoiceActorId)
                        .ToList();
                }) ?? [];
        }

        /// <summary>
        /// 获取特定番剧的CV-角色对照信息
        /// </summary>
        /// <param name="animeID">番剧的唯一标识符</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>CV-角色对照列表</returns>
        public async Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct)
        {
            return await GetCacheAsync($"characters:{animeID}", TimeSpan.FromDays(7),
                async () =>
                {
                    var result = await _apiClient.GetJsonAsync<List<RelatedCharacterResponse>>($"/v0/subjects/{animeID}/characters", ct).ConfigureAwait(false);
                    if (result is null) return new List<CharacterRole>();
                    var sorted = result.OrderBy(c => GetCharacterPriority(c.Relation)).ToList();
                    return sorted.Select(MapToCharacterRole).ToList();
                }) ?? [];
        }

        /// <summary>
        /// 按 Tag 搜索番剧（通过 Bangumi 搜索 API）。
        private const int SearchPageLimit = 20;      // API强制20

        /// <summary>
        /// 获取声优/人物参与的作品列表。
        /// </summary>
        /// <param name="personId">人物 ID。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>人物参与的作品列表。</returns>
        public async Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct)
        {
            return await GetCacheAsync($"person_works:{personId}", TimeSpan.FromDays(7),
                async () =>
                {
                    var result = await _apiClient.GetJsonAsync<List<RelatedSubjectResponse>>($"/v0/persons/{personId}/subjects", ct).ConfigureAwait(false);
                    if (result is null) return new List<PersonWork>();
                    return result
                        .Where(s => s.Type == 2) // 仅动画
                        .Select(s => new PersonWork(s.Id, ResolveTitle(s.NameCn, s.Name), s.Staff, ResolveImageUrl(s.Image)))
                        .ToList();
                }) ?? [];
        }

        /// <summary>
        /// 按关键词搜索一页番剧。
        /// </summary>
        /// <param name="keyword">搜索关键词。</param>
        /// <param name="offset">分页偏移量。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>(匹配的 Anime 列表, 总条数)。</returns>
        public async Task<(List<Anime> Results, int Total)> SearchByKeywordAsync(string keyword, int offset, CancellationToken ct)
        {
            var request = new SearchSubjectRequest(
                Keyword: keyword,
                Sort: "match",
                Filter: new SearchFilter(Type: new List<int> { 2 })
            );

            var url = $"/v0/search/subjects?limit={SearchPageLimit}&offset={offset}";
            var result = await _apiClient.PostJsonAsync<PagedSubjectResponse>(url, request, ct).ConfigureAwait(false);

            if (result?.Data == null || result.Data.Count == 0)
                return (new List<Anime>(), result?.Total ?? 0);

            var animes = result.Data
                .Select(item => MapFromSubject(item))
                .ToList();

            return (animes, result.Total);
        }

        /// <summary>
        /// 按 Bangumi Tag 搜索一页番剧。
        /// </summary>
        /// <param name="tag">标签名称</param>
        /// <param name="offset">分页偏移量</param>
        /// <param name="sort">排序方式："rank" / "date" / "match"</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="airDateFrom">起始日期（含），格式 "YYYY-MM-DD"，null 不限制</param>
        /// <param name="airDateTo">结束日期（不含），格式 "YYYY-MM-DD"，null 不限制</param>
        /// <returns>(匹配的 Anime 列表, 总条数)</returns>
        public async Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort, CancellationToken ct, string? airDateFrom = null, string? airDateTo = null)
        {
            var filter = new SearchFilter(
                Type: new List<int> { 2 },
                Tag: new List<string> { tag }
            );

            if (airDateFrom != null || airDateTo != null)
            {
                var airDate = new List<string>(2);
                if (airDateFrom != null) airDate.Add($">={airDateFrom}");
                if (airDateTo != null) airDate.Add($"<{airDateTo}");
                filter = filter with { AirDate = airDate };
            }

            var request = new SearchSubjectRequest(
                Sort: sort,
                Filter: filter
            );

            var url = $"/v0/search/subjects?limit={SearchPageLimit}&offset={offset}";
            var result = await _apiClient.PostJsonAsync<PagedSubjectResponse>(url, request, ct).ConfigureAwait(false);

            if (result?.Data == null || result.Data.Count == 0)
                return (new List<Anime>(), result?.Total ?? 0);

            var animes = result.Data
                .Select(item => MapFromSubject(item))
                .ToList();

            return (animes, result.Total);
        }
    }
}
