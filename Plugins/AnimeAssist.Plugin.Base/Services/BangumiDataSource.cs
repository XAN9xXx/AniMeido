using AnimeAssist.Contracts.Models;
using Microsoft.Extensions.Logging;
using AnimeAssist.Plugin.Base.Exceptions;
using AnimeAssist.Plugin.Base.Models.Bangumi;

namespace AnimeAssist.Plugin.Base.Services
{
    /// <summary>
    /// 用于从Bangumi API获取番剧数据的服务类，实现了IAnimeDataSource接口。
    /// </summary>
    public sealed class BangumiDataSource : Contracts.IAnimeDataSource
    {
        private readonly ILogger<BangumiDataSource> _logger;
        private const string ApiBase = "https://api.bgm.tv";
        private const string FallbackTitle = "Unknown Title";
        private const string FallbackDescription = "No description available.";
        private static readonly string? FallbackImageUrl = null;
        private static readonly IReadOnlyList<VoiceActor> FallbackCVs = Array.Empty<VoiceActor>();
        private static readonly IReadOnlyList<string> StudioFilter = new List<string> { "製作", "原作" , "企画" , "动画制作" , "发行" }; // API中Type 2 代表参与制作的商业实体，此处仅筛选制作/原作。
        private readonly BangumiApiClient _apiClient;



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
            var image = character.Images?.Grid is { Length: > 0 }  ? character.Images.Grid : FallbackImageUrl;
            return new CharacterRole(character.Id, character.Name, character.Summary, image, actors);
        }

        // 根据角色定位返回优先级，主角优先，配角次之，闲角最后。
        private static int GetCharacterPriority(string? relation) => relation switch
        {
            "主角" => 1,
            "配角" => 2,
            "闲角" => 3,
            _ => 3
        };

        // 将枚举转换为月份。
        private static int SeasonToMonth(Season season) => season switch
        {
            Season.Winter => 1, // Winter
            Season.Spring => 4, // Spring
            Season.Summer => 7, // Summer
            Season.Fall => 10, // Fall
            _ => throw new ArgumentOutOfRangeException(nameof(season))
        };

        // 将月份信息转换为季度信息。
        private static Season GetSeasonFromMonth(int month)
        {
            return month switch
            {
                1 or 2 or 3 => Season.Winter, // Winter
                4 or 5 or 6 => Season.Spring, // Spring
                7 or 8 or 9 => Season.Summer, // Summer
                10 or 11 or 12 => Season.Fall, // Fall
                _ => throw new ArgumentOutOfRangeException(nameof(month))
            };
        }

        internal BangumiDataSource(ILogger<BangumiDataSource> logger, BangumiApiClient apiClient)
        {
            ArgumentNullException.ThrowIfNull(logger, nameof(logger));
            ArgumentNullException.ThrowIfNull(apiClient, nameof(apiClient));
            _logger = logger;
            _apiClient = apiClient;
        }

        /// <summary>
        /// 从Bangumi API获取每周新番的日历数据，并将其解析为CalendarDayResponse对象列表。
        /// </summary>
        /// <param name="ct">取消令牌。</param>
        /// <returns>CalendarDayResponse对象列表。</returns>
        /// <exception cref="BangumiApiException">Bangumi API异常。</exception>
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

        // 判断条目的放送日期是否属于指定的年份和季度
        private static bool BelongsToSeason(CalendarItem item, int year, Season season)
        {
            if (!DateOnly.TryParse(item.AirDate, out var airDate))
            {
                return false;
            }
            return airDate.Year == year && GetSeasonFromMonth(airDate.Month) == season;
        }

        // 将CalendarItem对象映射为Anime对象，使用默认值处理缺失的信息，并记录无法解析的放送日期的警告日志
        private Anime MapToAnime(CalendarItem item, int year, int seasonMonth)
        {
            DateOnly? parsedDate = DateOnly.TryParse(item.AirDate, out var date) ? date : null;

            if (parsedDate is null)
            {
                _logger.LogWarning("Failed to parse air date for item {ItemId}: {AirDate}", item.Id, item.AirDate);
            }
            return new Anime
            (
                item.Id,
                item.NameCn ?? item.Name ?? FallbackTitle,
                null, // BangumiAPI的Calendar接口不提供制作公司信息，因此这里设置为null
                FallbackCVs,
                parsedDate,
                item.Image?.Grid is { Length: > 0 } grid ? grid : FallbackImageUrl,
                item.Summary ?? FallbackDescription,
                year,
                seasonMonth
                );
        }

        public async Task<List<Anime>> GetSeasonalAnimeAsync(int year, Season season, CancellationToken ct)
        {
            if (!Enum.IsDefined(season))
            {
                throw new ArgumentOutOfRangeException(nameof(season));
            }

            int seasonMonth = SeasonToMonth(season);
            var days = await FetchCalendarAsync(ct).ConfigureAwait(false);
            return days.SelectMany(day => day.Items)
                .Where(item => BelongsToSeason(item, year, season))
                .Select(item => MapToAnime(item, year, seasonMonth))
                .ToList();
        }

        public async Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct)
        {
            var url = $"{ApiBase}/v0/subjects/{animeID}";
            var result = await _apiClient.GetJsonAsync<SubjectResponse>(url, ct).ConfigureAwait(false);
            if (result is null) return null;
            
            DateOnly? airDate = DateOnly.TryParse(result.Date, out var d) ? d : null;

            return new Anime
            (
                result.Id,
                result.NameCn ?? result.Name ?? FallbackTitle,
                null, // BangumiAPI的Subject接口不提供制作公司信息，因此这里设置为null
                FallbackCVs,
                airDate,
                result.Image?.Medium is { Length: > 0 } Medium ? Medium : FallbackImageUrl,
                result.Summary ?? FallbackDescription,
                airDate?.Year ?? 0,
                airDate is { } ad ? SeasonToMonth(GetSeasonFromMonth(ad.Month)) : 0
            );
        }

        public async Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct)
        {
            var url = $"{ApiBase}/v0/subjects/{animeID}/persons";
            var result = await _apiClient.GetJsonAsync<List<RelatedPersonResponse>>(url, ct).ConfigureAwait(false);
            if (result is null)
            {
                return new List<Studio>();
            }
            return result.Where(person => person.Type == 2 && StudioFilter.Contains(person.Relation)) // API中Type 2 代表参与制作的商业实体，此处仅筛选制作/原作。
                .Select(person => new Studio(person.Id, person.Name, person.Images?.Grid))
                .ToList();
        }

        public async Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct)
        {
            var url = $"{ApiBase}/v0/subjects/{animeID}";
            var result = await _apiClient.GetJsonAsync<SubjectResponse>(url, ct).ConfigureAwait(false);
            if (result is null)
            {
                return new List<Tag>();
            }
            return result.MetaTags?.Select(tag => new Tag(tag))
                .ToList() ?? new List<Tag>();
        }

        public async Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct)
        {
            var url = $"{ApiBase}/v0/subjects/{animeID}/characters";
            var result = await _apiClient.GetJsonAsync<List<RelatedCharacterResponse>>(url, ct).ConfigureAwait(false);
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

        public async Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct)
        {
            var url = $"{ApiBase}/v0/subjects/{animeID}/characters";
            var result = await _apiClient.GetJsonAsync<List<RelatedCharacterResponse>>(url, ct).ConfigureAwait(false);
            if(result is null)
            {
                return new List<CharacterRole>();
            }
            var sorted = result.OrderBy(c => GetCharacterPriority(c.Relation)).ToList(); // 根据角色定位排序，主角优先
            return sorted.Select(MapToCharacterRole).ToList();
        }
    }

}
