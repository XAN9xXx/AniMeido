namespace AniMeido.Contracts.Models
{
    public enum Season
    {
        Winter = 1,
        Spring = 2,
        Summer = 3,
        Fall = 4,
    }

    public static class SeasonHelper
    {
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

        /// <exception cref="ArgumentOutOfRangeException">当传入的枚举值不在 Season 定义的范围内时引发。</exception>
        public static int ToMonth(Season season) => season switch
        {
            Season.Winter => 1,
            Season.Spring => 4,
            Season.Summer => 7,
            Season.Fall => 10,
            _ => throw new ArgumentOutOfRangeException(nameof(season))
        };

        public static (int year, Season season) GetCurrentSeason()
        {
            return (DateTime.Now.Year, FromMonth(DateTime.Now.Month));
        }
    }
}