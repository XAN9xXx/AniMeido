namespace AniMeido.Contracts.Models
{
    /// <summary>
    /// 记录用户对某部番剧的观看和兴趣状态。
    /// </summary>
    public enum AnimeTrackingStatus {
        /// <summary>无标记状态。</summary>
        None = 0,
        /// <summary>追番中，包括“正在追”和“未上映，计划追”。</summary>
        Watching = 1,
        /// <summary>补番中（仅限于老番）。</summary>
        PlanToWatch = 2,
        /// <summary>不感兴趣。</summary>
        NotInterested = 3,
        /// <summary>
        /// 关注
        /// </summary>
        Following = 4,
        /// <summary>
        /// 已看完
        /// </summary>
        Completed = 5,
        /// <summary>
        /// 弃坑
        /// </summary>
        Dropped = 6,
        /// <summary>
        /// 屏蔽
        /// </summary>
        Blocked = 7,
    }
}
