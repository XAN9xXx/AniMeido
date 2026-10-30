using AniMeido.Contracts.Models;

namespace AniMeido.Contracts
{
    /// <summary>
    /// 作品身份遵循 Anime.ID：正数是公共 ID，负数是个人档案保留的本地 ID。
    /// 组合数据源可为本地/不可用作品返回标题占位；远端无此作品时返回 null。
    /// personId 始终是公共人物 ID；无关联的人员/标签等资料返回空列表。
    /// 请求或存储失败仍可能抛出异常，不等同于没有资料。
    /// </summary>
    public interface IAnimeDataSource
    {
        /// <summary>读取指定年份和季度的作品；没有数据时返回空列表。</summary>
        Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct);

        /// <summary>
        /// 获取当前正在周期放送的动画日历。
        /// </summary>
        /// <returns>具有星期放送信息的动画列表。</returns>
        Task<List<Anime>> GetCurrentBroadcastScheduleAsync(
            CancellationToken ct);

        /// <summary>按当前作品身份读取详情；没有详情时返回 null，组合源可返回本地占位作品。</summary>
        Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct);

        /// <summary>读取作品的制作或企划组织；无关联资料时返回空列表。</summary>
        Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct);

        /// <summary>读取作品标签；无关联资料时返回空列表。</summary>
        Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct);

        /// <summary>读取作品声优；无关联资料时返回空列表。</summary>
        Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct);

        /// <summary>读取作品角色及其配音关联；无关联资料时返回空列表。</summary>
        Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct);

        /// <summary>
        /// 按 Tag 搜索番剧（通过资料服务），支持分页、排序和时间范围。
        /// </summary>
        /// <param name="sort">排序方式："rank" / "date" / "match"。</param>
        /// <param name="airDateFrom">起始日期（含），格式 "YYYY-MM-DD"，null 表示不限制。</param>
        /// <param name="airDateTo">结束日期（不含），格式 "YYYY-MM-DD"，null 表示不限制。</param>
        Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort, CancellationToken ct, string? airDateFrom = null, string? airDateTo = null);

        /// <summary>按公共人物 ID 读取相关作品；无关联资料时返回空列表。</summary>
        Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct);

        /// <summary>
        /// 按关键词搜索番剧（通过资料服务）。
        /// </summary>
        /// <returns>补全后的作品、远端总数和按原始页消费数推进的下一页偏移；空结果不代表已经到底。</returns>
        Task<(List<Anime> Results, int Total, int NextOffset)> SearchByKeywordAsync(string keyword, int offset, CancellationToken ct);
    }
}
