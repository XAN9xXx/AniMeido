namespace AniMeido.Plugin.Base.ViewModels;

public static class DesktopAnimeCardPresentation
{
    public static double CompactCoverHeightFor(double width) => Math.Round(width * 3 / 2);

    public static string ScoreForegroundResource(bool highScore)
        => highScore ? "DesktopOnAccentBrush" : "SystemControlForegroundChromeWhiteBrush";

    public static string FormatScore(double? score) => score is > 0
        ? score.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
        : string.Empty;
}
