namespace AniMeido.Plugin.Base.ViewModels;

/// <summary>番剧卡片的尺寸、评分分档与日期文字，不依赖 WinUI，便于测试。</summary>
public static class AnimeCardPresentation
{
    /// <summary>默认卡片宽度；封面按 3:4 跟随。</summary>
    public const double DefaultCardWidth = 150;

    /// <summary>评分达到这个值才用强调色，低于它用半透明深色。</summary>
    public const double HighScoreThreshold = 7.5;

    /// <summary>封面按 3:4 跟随卡片宽度；默认 150 宽对应 200 高。</summary>
    public static double CoverHeightFor(double width) => Math.Round(width * 4 / 3);

    public static bool IsHighScore(double score) => score >= HighScoreThreshold;

    /// <summary>“4月8日 · 周三”：年份已由所选季度确定，补上星期。</summary>
    public static string FormatSeasonDate(DateOnly date)
    {
        var weekday = date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek;
        return $"{date.Month}月{date.Day}日 · {AnimeListPresentation.GetWeekdayName(weekday)}";
    }
}
