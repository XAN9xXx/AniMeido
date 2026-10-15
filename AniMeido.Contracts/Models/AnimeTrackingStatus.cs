namespace AniMeido.Contracts.Models
{
    public enum AnimeTrackingStatus {
        None = 0,
        /// <summary>追番中，包括“正在追”和“未上映，计划追”。</summary>
        Watching = 1,
        /// <summary>补番中（仅限于老番）。</summary>
        PlanToWatch = 2,
        NotInterested = 3,
        Following = 4,
        Completed = 5,
        Dropped = 6,
        Blocked = 7,
    }
}
