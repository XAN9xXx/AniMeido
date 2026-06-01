using AniMeido.Contracts.Models;

namespace AniMeido.Contracts
{
    public interface IAnimeDataSource
    {
        /// <summary>
        /// 获取指定时间段的条目，并将其映射为Anime对象列表返回。
        /// </summary>
        /// <param name="year">年份。</param>
        /// <param name="season">季度。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>Anime对象列表。</returns>
        Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct);

        /// <summary>
        /// 获取某部特定番剧的详细信息。
        /// </summary>
        /// <param name="animeID">Bangumi 条目 ID。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>番剧详细信息；如未找到则返回null。</returns>
        Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct);

        /// <summary>
        /// 获取指定番剧的出版社 / 企划信息。
        /// </summary>
        /// <param name="animeID">Bangumi 条目 ID。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>出版社 / 企划信息列表；如未找到则返回空列表。</returns>
        Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct);

        /// <summary>
        /// 获取指定番剧的标签列表。
        /// </summary>
        /// <param name="animeID">Bangumi 条目 ID。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>标签列表；如未找到则返回空列表。</returns>
        Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct);

        /// <summary>
        /// 获取指定番剧的声优列表。
        /// </summary>
        /// <param name="animeID">Bangumi 条目 ID。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>声优列表；如未找到则返回空列表。</returns>
        Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct);

        /// <summary>
        /// 获取指定番剧的角色-声优对照列表。
        /// </summary>
        /// <param name="animeID">Bangumi 条目 ID。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>角色-声优对照列表；如未找到则返回空列表。</returns>
        Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct);

        /// <summary>
        /// 按 Tag 搜索番剧（通过 Bangumi 搜索 API），支持分页、排序和时间范围。
        /// </summary>
        /// <param name="tag">标签名称。</param>
        /// <param name="offset">分页偏移量。</param>
        /// <param name="sort">排序方式："rank" / "date" / "match"。</param>
        /// <param name="ct">取消令牌。</param>
        /// <param name="airDateFrom">起始日期（含），格式 "YYYY-MM-DD"，null 表示不限制。</param>
        /// <param name="airDateTo">结束日期（不含），格式 "YYYY-MM-DD"，null 表示不限制。</param>
        /// <returns>(结果列表, 总条数)。</returns>
        Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort, CancellationToken ct, string? airDateFrom = null, string? airDateTo = null);

        /// <summary>
        /// 获取声优/人物参与的作品列表。
        /// </summary>
        /// <param name="personId">人物 ID。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>人物参与的作品列表。</returns>
        Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct);

        /// <summary>
        /// 按关键词搜索番剧（通过 Bangumi 搜索 API）。
        /// </summary>
        /// <param name="keyword">搜索关键词。</param>
        /// <param name="offset">分页偏移量。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>(结果列表, 总条数)。</returns>
        Task<(List<Anime> Results, int Total)> SearchByKeywordAsync(string keyword, int offset, CancellationToken ct);
    }
}
