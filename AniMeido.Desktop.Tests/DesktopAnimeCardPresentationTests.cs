using AniMeido.Plugin.Base.ViewModels;
using Xunit;

namespace AniMeido.Desktop.Tests;

public sealed class DesktopAnimeCardPresentationTests
{
    [Theory]
    [InlineData(92, 138)]
    [InlineData(100, 150)]
    [InlineData(150, 225)]
    public void CompactCoverKeepsTheTwoByThreePosterRatio(double width, double expectedHeight)
        => Assert.Equal(expectedHeight, DesktopAnimeCardPresentation.CompactCoverHeightFor(width));

    [Theory]
    [InlineData(8.506941192949984, "8.5")]
    [InlineData(8.36, "8.4")]
    [InlineData(8.0, "8.0")]
    [InlineData(null, "")]
    [InlineData(0.0, "")]
    public void CatalogScoreUsesOneDecimalAndKeepsMissingScoresEmpty(double? score, string expected)
        => Assert.Equal(expected, DesktopAnimeCardPresentation.FormatScore(score));

    [Theory]
    [InlineData(true, "DesktopOnAccentBrush")]
    [InlineData(false, "SystemControlForegroundChromeWhiteBrush")]
    public void ScoreTextColorMatchesItsAccentOrDarkOverlayBackground(bool highScore, string resource)
        => Assert.Equal(resource, DesktopAnimeCardPresentation.ScoreForegroundResource(highScore));
}
