using AniMeido.Contracts.Models;
using Microsoft.Extensions.Logging;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Models.Bangumi;
using System.Diagnostics;

namespace AniMeido.Plugin.Base.Services
{
    /// <summary>
    /// 用于从Bangumi API获取番剧数据的服务类，实现了IAnimeDataSource接口。
    /// </summary>
    public sealed class BangumiDataSource : Contracts.IAnimeDataSource
    {
        private readonly BangumiApiClient _apiClient;
        private readonly ILogger<BangumiDataSource> _logger;
        private const string ApiBase = "https://api.bgm.tv";
        private const string FallbackTitle = "不好，标题走丢了Q^Q";
        private const string FallbackDescription = "No description available.";
        private static readonly string? FallbackImageUrl = null;
        private static readonly IReadOnlyList<VoiceActor> FallbackCVs = Array.Empty<VoiceActor>();
        private static readonly IReadOnlyList<string> StudioFilter = new List<string> { "製作", "原作", "企画", "动画制作", "发行" }; // API中Type 2 代表参与制作的商业实体，此处仅筛选制作/原作。



        public BangumiDataSource(ILogger<BangumiDataSource> logger, BangumiApiClient apiClient)
        {
            ArgumentNullException.ThrowIfNull(logger, nameof(logger));
            ArgumentNullException.ThrowIfNull(apiClient, nameof(apiClient));
            _logger = logger;
            _apiClient = apiClient;
        }



        // 以年-季度为条件从API筛选番剧，返回Anime列表的辅助方法
        private async Task<List<Anime>> FetchByBrowse(int year, int seasonMonth, CancellationToken ct)
        {
            var result = await _apiClient.GetJsonAsync<PagedSubjectResponse>($"{ApiBase}/v0/subjects?type=2&year={year}&month={seasonMonth}&limit=50", ct);
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
            var result = await _apiClient.GetJsonAsync<List<CalendarDayResponse>>($"{ApiBase}/calendar", ct).ConfigureAwait(false);
            if (result is null)
            {
                _logger.LogError("Bangumi calendar API returned null.");
                throw new BangumiApiException("Bangumi calendar API returned null.");
            }
            return result;
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
                null, // BangumiAPI的Calendar接口不提供制作公司信息，因此这里设置为null
                FallbackCVs,
                parsedDate,
                item.Images?.Large is { Length: > 0 } large ? large : FallbackImageUrl,
                item.Summary ?? FallbackDescription,
                year,
                seasonMonth,
                item.AirWeekday
                );
        }



        // 将ActorInfo映射为一个VoiceActor
        private static VoiceActor MapToVoiceActor(ActorInfo actor)
        {
            return new VoiceActor(actor.Id, actor.Name, actor.Image?.Grid is { Length: > 0 } grid ? grid : FallbackImageUrl);
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
            var image = character.Images?.Grid is { Length: > 0 } ? character.Images.Grid : FallbackImageUrl;
            return new CharacterRole(character.Id, character.Name, character.Summary, image, actors);
        }



        // 优先使用中文译名，其次日文原名，最后后备文字；同时处理 API 返回空字符串的情况
        private static string ResolveTitle(string? nameCn, string? name)
            => !string.IsNullOrWhiteSpace(nameCn) ? nameCn
             : !string.IsNullOrWhiteSpace(name) ? name
             : FallbackTitle;

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
                null,          // Studio 由单独的 GetStudioAsync() 获取
                FallbackCVs,   // CVs 由单独的 GetCVsAsync() 获取
                parsedDate,
                item.Images?.Large is { Length: > 0 } large ? large : FallbackImageUrl,
                item.Summary ?? FallbackDescription,
                resolvedYear,
                resolvedSeasonMonth
                );
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
            {
                throw new ArgumentOutOfRangeException(nameof(season));
            }

            int seasonMonth = SeasonHelper.ToMonth(season);
            var days = await FetchCalendarAsync(ct).ConfigureAwait(false);
            List<Anime> animes = days.SelectMany(day => day.Items)
                                    .Where(item => BelongsToSeason(item, year, season))
                                    .Select(item => MapToAnime(item, year, seasonMonth))
                                    .ToList();
            if (animes.Count > 0)
                return animes;
            if (year < DateTime.Now.Year)
                return await FetchByBrowse(year, seasonMonth, ct);
            var currentSeason = SeasonHelper.FromMonth(DateTime.Now.Month);
            if (season != currentSeason)
                return await FetchByBrowse(year, seasonMonth, ct);
            return [];
        }

        /// <summary>
        /// 获取特定番剧的详细信息
        /// </summary>
        /// <param name="animeID">番剧的唯一标识符</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>番剧详情信息</returns>
        public async Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct)
        {
            var result = await _apiClient.GetJsonAsync<SubjectResponse>($"{ApiBase}/v0/subjects/{animeID}", ct).ConfigureAwait(false);
            if (result is null) return null;

            return MapFromSubject(result);
        }

        /// <summary>
        /// 获取特定番剧的Studio信息
        /// </summary>
        /// <param name="animeID">番剧的唯一标识符</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>Studio列表</returns>
        public async Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct)
        {
            var result = await _apiClient.GetJsonAsync<List<RelatedPersonResponse>>($"{ApiBase}/v0/subjects/{animeID}/persons", ct).ConfigureAwait(false);
            if (result is null) return new List<Studio>();
            return result.Where(person => person.Type == 2 && StudioFilter.Contains(person.Relation)) // API中Type 2 代表参与制作的商业实体，此处仅筛选制作/原作。
                .Select(person => new Studio(person.Id, person.Name, person.Images?.Grid))
                .ToList();
        }

        /// <summary>
        /// 获取特定番剧的Tag信息
        /// </summary>
        /// <param name="animeID">番剧的唯一标识符</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>Tag列表</returns>
        public async Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct)
        {
            var result = await _apiClient.GetJsonAsync<SubjectResponse>($"{ApiBase}/v0/subjects/{animeID}", ct).ConfigureAwait(false);
            if (result is null)
            {
                return new List<Tag>();
            }
            return result.MetaTags?.Select(tag => new Tag(tag))
                .ToList() ?? new List<Tag>();
        }

        /// <summary>
        /// 获取特定番剧的CV信息
        /// </summary>
        /// <param name="animeID">番剧的唯一标识符</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>VoiceActor列表</returns>
        public async Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct)
        {
            var result = await _apiClient.GetJsonAsync<List<RelatedCharacterResponse>>($"{ApiBase}/v0/subjects/{animeID}/characters", ct).ConfigureAwait(false);
            if (result is null)
            {
                return new List<VoiceActor>();
            }
            var sorted = result.OrderBy(c => GetCharacterPriority(c.Relation)).ToList(); // 根据角色定位排序，主角优先
            return sorted.Select(character => MapToVoiceActor(character.Actors))
                .SelectMany(cvs => cvs)
                .DistinctBy(v => v.VoiceActorId) // 去重，确保同一声优只出现一次
                .ToList();
        }

        /// <summary>
        /// 获取特定番剧的CV-角色对照信息
        /// </summary>
        /// <param name="animeID">番剧的唯一标识符</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>CV-角色对照列表</returns>
        public async Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct)
        {
            var result = await _apiClient.GetJsonAsync<List<RelatedCharacterResponse>>($"{ApiBase}/v0/subjects/{animeID}/characters", ct).ConfigureAwait(false);
            if (result is null)
            {
                return new List<CharacterRole>();
            }
            var sorted = result.OrderBy(c => GetCharacterPriority(c.Relation)).ToList(); // 根据角色定位排序，主角优先
            return sorted.Select(MapToCharacterRole).ToList();
        }
    }

}
