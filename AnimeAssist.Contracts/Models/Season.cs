using System.Security.Cryptography.X509Certificates;

namespace AniMeido.Contracts.Models
{
    /// <summary>
    /// 季度的枚举类型
    /// </summary>
    /// <remarks>
    /// 用于以语义化的方式表示季度，避免使用数字。
    /// ||
    /// Winter=1, Spring=2, Summer=3, Fall=4
    /// </remarks>
    public enum Season
    {
        Winter = 1,
        Spring = 2,
        Summer = 3,
        Fall = 4,
    }

    public static class SeasonHelper
    {
        public static Season FromMonth(int month)
        {
            return month switch
            {
                >= 1 and <= 3 => Season.Winter,
                >= 4 and <= 6 => Season.Spring,
                >= 7 and <= 9 => Season.Summer,
                >= 10 and <= 12 => Season.Fall,
                _ => throw new ArgumentOutOfRangeException(nameof(month))
            };
        }

        public static (int year, Season season) GetCurrentSeason()
        {
            return (DateTime.Now.Year, FromMonth(DateTime.Now.Month));
        }
    }
}
