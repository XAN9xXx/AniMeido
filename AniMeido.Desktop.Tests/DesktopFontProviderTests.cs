using AniMeido.App.Services;
using Windows.UI.Text;
using Xunit;

namespace AniMeido.Desktop.Tests;

public sealed class DesktopFontProviderTests
{
    [Theory]
    [InlineData('桐', 17, "Desktop WenKai Reading")]
    [InlineData('桐', 34, "Desktop WenKai")]
    [InlineData('衬', 17, "Desktop WenKai")]
    [InlineData('齉', 17, "Desktop WenKai")]
    public async Task BundledChineseFontsKeepTheSmallTextRouteAndFullCoverage(char character, float size, string family)
    {
        var provider = new DesktopFontProvider(Fonts()) { UseSerif = true };
        var font = await provider.MatchCharacterAsync(character, new FontWeight { Weight = 400 }, FontStretch.Normal, FontStyle.Normal, size);
        Assert.NotNull(font);
        Assert.Equal(family, font.FamilyName);
        Assert.True(font.ContainsGlyph(character));
        Assert.NotEqual((ushort)0, font.GetGlyphIndex(character));
        var again = await provider.MatchCharacterAsync(character, new FontWeight { Weight = 400 }, FontStretch.Normal, FontStyle.Normal, size);
        Assert.Same(font, again);
    }

    [Fact]
    public async Task ReturningToSystemFontsRestoresSystemFallback()
    {
        var provider = new DesktopFontProvider(Fonts()) { UseSerif = true };
        var weight = new FontWeight { Weight = 400 };
        var serif = await provider.MatchCharacterAsync('中', weight, FontStretch.Normal, FontStyle.Normal, 17);
        provider.UseSerif = false;
        var system = await provider.MatchCharacterAsync('中', weight, FontStretch.Normal, FontStyle.Normal, 17);
        Assert.NotNull(system);
        Assert.NotEqual(serif!.FamilyName, system.FamilyName);
        provider.UseSerif = true;
        Assert.Same(serif, await provider.MatchCharacterAsync('中', weight, FontStretch.Normal, FontStyle.Normal, 17));
    }

    [Fact]
    public void EnglishLoadsTheOriginalFontFileAndShapesLettersAndNumbers()
    {
        var provider = new DesktopFontProvider(Fonts());
        using var stream = File.OpenRead(Path.Combine(Fonts(), "DesktopSerif.ttf"));
        using var file = provider.LoadFontFile(stream);
        var font = file.CreateFont("Desktop Serif", new FontWeight { Weight = 400 }, FontStretch.Normal, FontStyle.Normal, 17)!;
        Assert.Equal("Desktop Serif Text", font.FamilyName);
        Assert.All("March 8.36", character => Assert.NotEqual((ushort)0, font.GetGlyphIndex(character)));
        Assert.NotEmpty(font.Shape("March 8.36", Uno.UI.Composition.Drawing.TextDirection.LeftToRight).Glyphs);
    }

    private static string Fonts()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AniMeido.slnx")))
                return Path.Combine(directory.FullName, "AniMeido.Desktop", "Assets", "Fonts");
        throw new DirectoryNotFoundException("找不到 AniMeido.slnx。");
    }
}
