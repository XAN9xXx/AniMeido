namespace AniMeido.Contracts.Models
{
    /// <summary>
    /// 季度的枚举类型
    /// </summary>
    /// <remarks>
    /// 用于以语义化的方式表示季度，避免使用数字。
    /// </remarks>
    public enum Season
    {
        Winter = 1,
        Spring = 2,
        Summer = 3,
        Fall = 4,
    }

    /// <summary>
    /// 用于将月份int转换为季度enum的公共方法类
    /// </summary>
    public static class SeasonHelper
    {
        /// <summary>
        /// 将月份 int 映射为季度 enum。
        /// </summary>
        /// <param name="month">要转换的的月份int</param>
        /// <returns>enum型季度信息</returns>
        /// <exception cref="ArgumentOutOfRangeException">接收超出范围的数字时抛出</exception>
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

        /// <summary>
        /// 将 Season 枚举映射为该季节的起始月份编号。   
        /// </summary>
        /// <param name="season">要转换的 Season 枚举值。</param>
        /// <returns>表示季节起始月份的整数（1–12）。例如 Winter→1、Spring→4、Summer→7、Fall→10。</returns>
        /// <exception cref="ArgumentOutOfRangeException">当传入的枚举值不在 Season 定义的范围内时引发。</exception>
        public static int ToMonth(Season season) => season switch
        {
            Season.Winter => 1, // Winter
            Season.Spring => 4, // Spring
            Season.Summer => 7, // Summer
            Season.Fall => 10, // Fall
            _ => throw new ArgumentOutOfRangeException(nameof(season))
        };

        /// <summary>
        /// 用于获取系统时间下的季度信息
        /// </summary>
        /// <returns>(系统时间下的年份,调用FromMonth方法获取的系统时间下的季度)</returns>
        public static (int year, Season season) GetCurrentSeason()
        {
            return (DateTime.Now.Year, FromMonth(DateTime.Now.Month));
        }
    }
}