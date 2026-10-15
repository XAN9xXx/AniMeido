using AniMeido.Contracts.Models;

namespace AniMeido.Contracts
{
    /// <summary>
    /// animeID、personId 均为 Bangumi ID。未找到时详情返回 null，列表返回空列表。
    /// </summary>
    public interface IAnimeDataSource
    {
        Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct);

        /// <summary>
        /// 获取当前正在周期放送的动画日历。
        /// </summary>
        /// <returns>具有星期放送信息的动画列表。</returns>
        Task<List<Anime>> GetCurrentBroadcastScheduleAsync(
            CancellationToken ct);

        Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct);

        Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct);

        Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct);

        Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct);

        Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct);

        /// <summary>
        /// 按 Tag 搜索番剧（通过 Bangumi 搜索 API），支持分页、排序和时间范围。
        /// </summary>
        /// <param name="sort">排序方式："rank" / "date" / "match"。</param>
        /// <param name="airDateFrom">起始日期（含），格式 "YYYY-MM-DD"，null 表示不限制。</param>
        /// <param name="airDateTo">结束日期（不含），格式 "YYYY-MM-DD"，null 表示不限制。</param>
        Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort, CancellationToken ct, string? airDateFrom = null, string? airDateTo = null);

        Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct);

        /// <summary>
        /// 按关键词搜索番剧（通过 Bangumi 搜索 API）。
        /// </summary>
        Task<(List<Anime> Results, int Total)> SearchByKeywordAsync(string keyword, int offset, CancellationToken ct);
    }
}
