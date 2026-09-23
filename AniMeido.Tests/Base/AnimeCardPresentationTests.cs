using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

/// <summary>卡片的评分分档、封面比例、日期文字与形态角标。</summary>
public sealed class AnimeCardPresentationTests
{
    [Theory]
    [InlineData(7.5, true)]
    [InlineData(8.9, true)]
    [InlineData(7.4, false)]
    [InlineData(5.0, false)]
    public void IsHighScore_UsesSevenPointFiveThreshold(double score, bool expected)
        => Assert.Equal(expected, AnimeCardPresentation.IsHighScore(score));

    [Theory]
    [InlineData(150, 200)]
    [InlineData(180, 240)]
    [InlineData(165, 220)]
    public void CoverHeightFor_KeepsThreeByFour(double width, double height)
        => Assert.Equal(height, AnimeCardPresentation.CoverHeightFor(width));

    [Theory]
    [InlineData(2026, 4, 8, "4月8日 · 周三")]
    [InlineData(2026, 4, 5, "4月5日 · 周日")]
    [InlineData(2026, 6, 13, "6月13日 · 周六")]
    public void FormatSeasonDate_ShowsMonthDayAndWeekday(int year, int month, int day, string expected)
        => Assert.Equal(expected, AnimeCardPresentation.FormatSeasonDate(new DateOnly(year, month, day)));

    [Theory]
    [InlineData(AnimeMediaFormat.Television, null)]
    [InlineData(AnimeMediaFormat.Unknown, null)]
    [InlineData(AnimeMediaFormat.Ona, "Web")]
    [InlineData(AnimeMediaFormat.Movie, "剧场版")]
    [InlineData(AnimeMediaFormat.Ova, "OVA")]
    public void GetMediaFormatBadgeText_SkipsTelevisionAndUnknown(AnimeMediaFormat format, string? expected)
        => Assert.Equal(expected, AnimeReleaseClassifier.GetMediaFormatBadgeText(format));
}
