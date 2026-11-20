using AniMeido.App.Services;
using Xunit;

namespace AniMeido.Desktop.Tests;

public sealed class DesktopButtonColorsTests
{
    [Theory]
    [InlineData(DesktopVisualStyle.Fluent10, false, "#0078D7", "#FFFFFF")]
    [InlineData(DesktopVisualStyle.Fluent10, true, "#0078D7", "#FFFFFF")]
    [InlineData(DesktopVisualStyle.Fluent11, false, "#005FB8", "#FFFFFF")]
    [InlineData(DesktopVisualStyle.Fluent11, true, "#60CDFF", "#001F33")]
    [InlineData(DesktopVisualStyle.Paper, false, "#B9553A", "#FFFFFF")]
    [InlineData(DesktopVisualStyle.Paper, true, "#D97757", "#1F1F1F")]
    public void PrimaryButtonsUseTheDocumentedSurfaceAndText(
        DesktopVisualStyle visual, bool dark, string background, string foreground)
    {
        var colors = DesktopAppearanceButtonColors.For(visual, dark);
        Assert.Equal(background, colors.Primary);
        Assert.Equal(foreground, colors.OnPrimary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModernButtonsHaveADarkerBottomEdge(bool dark)
    {
        var colors = DesktopAppearanceButtonColors.For(DesktopVisualStyle.Fluent11, dark);
        Assert.NotEqual(colors.ControlEdge, colors.ControlBottomEdge);
        Assert.NotEqual(colors.PrimaryEdge, colors.PrimaryBottomEdge);
    }

    [Theory]
    [InlineData(DesktopVisualStyle.Fluent10, false)]
    [InlineData(DesktopVisualStyle.Fluent10, true)]
    [InlineData(DesktopVisualStyle.Paper, false)]
    [InlineData(DesktopVisualStyle.Paper, true)]
    public void FlatThemesKeepTheirTopAndBottomEdgesEqual(DesktopVisualStyle visual, bool dark)
    {
        var colors = DesktopAppearanceButtonColors.For(visual, dark);
        Assert.Equal(colors.ControlEdge, colors.ControlBottomEdge);
        Assert.Equal(colors.PrimaryEdge, colors.PrimaryBottomEdge);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PaperPrimaryTextStaysReadableThroughoutHoverAndPress(bool dark)
    {
        var colors = DesktopAppearanceButtonColors.For(DesktopVisualStyle.Paper, dark);
        foreach (var background in new[] { colors.Primary, colors.Hover, colors.Pressed })
            Assert.True(Contrast(colors.OnPrimary, background) >= 4.5, background);
    }

    private static double Contrast(string first, string second)
    {
        static double Luminance(string hex)
        {
            var linear = Enumerable.Range(0, 3).Select(index => Convert.ToByte(hex.Substring(index * 2 + 1, 2), 16) / 255d)
                .Select(value => value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4)).ToArray();
            return .2126 * linear[0] + .7152 * linear[1] + .0722 * linear[2];
        }
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}
