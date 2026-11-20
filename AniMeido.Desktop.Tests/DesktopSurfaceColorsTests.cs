using AniMeido.App.Services;
using Xunit;

namespace AniMeido.Desktop.Tests;

public sealed class DesktopSurfaceColorsTests
{
    public static TheoryData<DesktopVisualStyle, bool> Modes => new()
    {
        { DesktopVisualStyle.Fluent10, false }, { DesktopVisualStyle.Fluent10, true },
        { DesktopVisualStyle.Fluent11, false }, { DesktopVisualStyle.Fluent11, true },
        { DesktopVisualStyle.Paper, false }, { DesktopVisualStyle.Paper, true },
    };

    [Theory]
    [MemberData(nameof(Modes))]
    public void PageSectionsAndNestedCardsHaveDistinctSurfaces(DesktopVisualStyle style, bool dark)
    {
        var palette = DesktopAppearancePalette.For(style, dark);
        Assert.NotEqual(palette.Navigation, palette.Page);
        Assert.NotEqual(palette.Page, palette.Surface);
        Assert.NotEqual(palette.Surface, palette.Card);
        Assert.NotEqual(palette.Selected, palette.Card);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void FieldsAndPopoversRemainSeparateFromTheirContainingSection(DesktopVisualStyle style, bool dark)
    {
        var palette = DesktopAppearancePalette.For(style, dark);
        Assert.NotEqual(palette.Surface, palette.Field);
        Assert.NotEqual(palette.Page, palette.Overlay);
        Assert.NotEqual(palette.Field, palette.Selected);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void PrimaryAndSecondaryTextStayReadableOnEveryContentSurface(DesktopVisualStyle style, bool dark)
    {
        var palette = DesktopAppearancePalette.For(style, dark);
        foreach (var background in new[] { palette.Page, palette.Surface, palette.Card, palette.Field, palette.Overlay, palette.Selected })
        foreach (var foreground in new[] { palette.Text, palette.Muted })
        {
            var light = Math.Max(Luminance(foreground), Luminance(background));
            var dim = Math.Min(Luminance(foreground), Luminance(background));
            Assert.True((light + 0.05) / (dim + 0.05) >= 4.5, $"{style}/{dark}: {foreground} on {background}");
        }
    }

    [Theory]
    [InlineData(DesktopVisualStyle.Fluent10, false, "#FFFFFF", "#0078D7")]
    [InlineData(DesktopVisualStyle.Fluent10, true, "#000000", "#0078D7")]
    [InlineData(DesktopVisualStyle.Fluent11, false, "#F9F9F9", "#005FB8")]
    [InlineData(DesktopVisualStyle.Fluent11, true, "#262626", "#60CDFF")]
    [InlineData(DesktopVisualStyle.Paper, false, "#FAF9F5", "#B9553A")]
    [InlineData(DesktopVisualStyle.Paper, true, "#1F1F1F", "#D97757")]
    public void LayeringPreservesTheApprovedPageAndAccent(DesktopVisualStyle style, bool dark, string page, string accent)
    {
        var palette = DesktopAppearancePalette.For(style, dark);
        Assert.Equal(page, palette.Page);
        Assert.Equal(accent, palette.Accent);
    }

    private static double Luminance(string hex)
    {
        static double Linear(byte value)
        {
            var channel = value / 255d;
            return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(Convert.ToByte(hex.Substring(1, 2), 16))
            + 0.7152 * Linear(Convert.ToByte(hex.Substring(3, 2), 16))
            + 0.0722 * Linear(Convert.ToByte(hex.Substring(5, 2), 16));
    }
}
