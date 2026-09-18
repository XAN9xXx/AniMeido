using AniMeido.Contracts.Models;

namespace AniMeido.Plugin.Base.Models
{
    /// <summary>
    /// 打开番剧库时直接定位到的季度（放送日历的番剧时光机使用）。
    /// 用 record 保证相同季度的两次导航被视为同一目标，不会重复压入返回栈。
    /// </summary>
    public sealed record PastSeasonTarget(int Year, Season Season);
}
