namespace AniMeido.Contracts.Models
{
    /// <summary>放送季度的顺序编号，1 至 4 分别为冬、春、夏、秋，不是季度月份。</summary>
    public enum Season
    {
        Winter = 1,
        Spring = 2,
        Summer = 3,
        Fall = 4,
    }

    /// <summary>季度顺序编号与公历月份的转换。</summary>
    public static class SeasonHelper
    {
        /// <summary>将 1 至 12 月转换为所属季度。</summary>
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

        /// <summary>取得季度首月：1、4、7 或 10 月。</summary>
        /// <exception cref="ArgumentOutOfRangeException">当传入的枚举值不在 Season 定义的范围内时引发。</exception>
        public static int ToMonth(Season season) => season switch
        {
            Season.Winter => 1,
            Season.Spring => 4,
            Season.Summer => 7,
            Season.Fall => 10,
            _ => throw new ArgumentOutOfRangeException(nameof(season))
        };

        /// <summary>使用同一次本地时间采样返回当前年份与季度。</summary>
        public static (int year, Season season) GetCurrentSeason()
        {
            var now = DateTime.Now;
            return (now.Year, FromMonth(now.Month));
        }
    }
}