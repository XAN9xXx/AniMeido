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
        Task<List<Anime>> GetSeasonalAnimeAsync(int year, Season season, CancellationToken ct);

        /// <summary>
        /// 获取某部特定番剧的详细信息。
        /// </summary>
        /// <param name="animeID">Bangumi 条目 ID。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>番剧详细信息；如未找到则返回null。</returns>
        Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct);

        /// <summary>
        /// 获取指定番剧的制作公司 / 工作室信息。
        /// </summary>
        /// <param name="animeID">Bangumi 条目 ID。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>制作公司 / 工作室信息列表；如未找到则返回空列表。</returns>
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
    }
}
