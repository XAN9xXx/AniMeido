namespace AniMeido.Contracts.Models;

/// <summary>
/// 动画条目的发行形态。该分类描述媒介形态，不描述是否属于既有系列。
/// </summary>
public enum AnimeMediaFormat
{
    Unknown = 0,
    Television = 1,
    Ova = 2,
    Movie = 3,
    Ona = 5,
}
