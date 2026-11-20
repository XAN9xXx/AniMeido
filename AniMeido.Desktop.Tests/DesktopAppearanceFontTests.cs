using AniMeido.App.Services;
using System.Xml.Linq;
using Xunit;

namespace AniMeido.Desktop.Tests;

public sealed class DesktopAppearanceFontTests
{
    [Theory]
    [InlineData(0, "System")]
    [InlineData(1, "Serif")]
    [InlineData(2, "Classic")]
    public void SavedFontValuesKeepTheirNamesAndNumericIdentity(int value, string name)
    {
        Assert.Equal(name, ((DesktopFontStyle)value).ToString());
        Assert.Equal(value, (int)Enum.Parse<DesktopFontStyle>(name));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    public void ChoiceOrderIsClassicModernAndSerifWithoutChangingSavedValues(int index, int value)
    {
        var style = (DesktopFontStyle)value;
        Assert.Equal(style, DesktopAppearanceFont.FromChoiceIndex(index));
        Assert.Equal(index, DesktopAppearanceFont.ToChoiceIndex(style));
    }

    [Theory]
    [InlineData(2, 32, 300, 20, 400, 14, 13, 15, 1.75)]
    [InlineData(0, 28, 600, 15, 600, 14, 13, 15, 1.72)]
    [InlineData(1, 34, 400, 17, 400, 16, 14, 17, 1.8)]
    public void FontRolesUseTheReviewedSizesAndWeightsIndependentOfVisualStyle(
        int style, double title, ushort titleWeight, double section, ushort sectionWeight,
        double control, double data, double body, double lineHeight)
    {
        var font = DesktopAppearanceFont.ForStyle((DesktopFontStyle)style);
        Assert.Equal(title, font.TitleSize);
        Assert.Equal(titleWeight, font.TitleWeight);
        Assert.Equal(section, font.SectionSize);
        Assert.Equal(sectionWeight, font.SectionWeight);
        Assert.Equal(control, font.ControlSize);
        Assert.Equal(data, font.DataSize);
        Assert.Equal(body, font.BodySize);
        Assert.Equal(lineHeight, font.BodyLineHeight);
    }

    [Theory]
    [InlineData(2, "Segoe UI")]
    [InlineData(0, "Segoe UI Variable Text")]
    public void ClassicAndModernFamiliesUseASingleSkiaFamily(int style, string expected)
    {
        var font = DesktopAppearanceFont.ForStyle((DesktopFontStyle)style);
        Assert.Equal(expected, font.Family);
        Assert.Equal(expected, font.ReadingFamily);
    }

    [Fact]
    public void SerifReadingAndUiKeepTheSameBundledEnglishFont()
    {
        var font = DesktopAppearanceFont.ForStyle(DesktopFontStyle.Serif);
        Assert.Equal("ms-appx:///Assets/Fonts/DesktopSerif.ttf#Desktop Serif", font.Family);
        Assert.Equal(font.Family, font.ReadingFamily);
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public void FontChoiceShowsThreePublicNamesInTheMappedOrder()
    {
        var document = XDocument.Load(Path.Combine(FindRepository(), "AniMeido.Desktop", "Views", "AppSettingsPage.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var choice = Assert.Single(document.Descendants(), element =>
            (string?)element.Attribute(xaml + "Name") == "FontStyleChoice");
        Assert.Equal(new[] { "经典黑体", "现代黑体", "衬线体" },
            choice.Elements(xaml + "String").Select(element => element.Value));
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AniMeido.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("找不到 AniMeido.slnx。");
    }
}
