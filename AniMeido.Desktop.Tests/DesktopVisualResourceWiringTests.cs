using System.Xml.Linq;
using Xunit;

namespace AniMeido.Desktop.Tests;

public sealed class DesktopVisualResourceWiringTests
{
    [Theory]
    [Trait("Category", "Mechanism")]
    [InlineData("TodayPage.xaml", "56", "84")]
    [InlineData("TodayPage.xaml", "32", "44")]
    [InlineData("TodayPage.xaml", "48", "68")]
    [InlineData("GlobalSearchPage.xaml", "40", "60")]
    [InlineData("RecommendationPage.xaml", "210", "315")]
    [InlineData("ArchivePage.xaml", "180", "104")]
    [InlineData("ManagementPage.xaml", "44", "60")]
    public void ImageContainersUseTheReviewedCoverRadius(string file, string width, string height)
    {
        var page = ReadDesktopPage(file);
        var cover = Assert.Single(page.Descendants(), element => element.Name.LocalName == "Border"
            && (string?)element.Attribute("Width") == width && (string?)element.Attribute("Height") == height);
        Assert.Equal("{ThemeResource DesktopCoverRadius}", (string?)cover.Attribute("CornerRadius"));
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public void RecommendationCandidatesUseTheSharedCardWithExplicitAnime()
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var candidates = Assert.Single(ReadDesktopPage("RecommendationPage.xaml").Descendants(),
            element => (string?)element.Attribute(xaml + "Name") == "RecommendationCandidates");
        var card = Assert.Single(candidates.Descendants(), element => element.Name.LocalName == "AnimeCard");
        Assert.Equal("{x:Bind Anime, Mode=OneWay}", (string?)card.Attribute("Anime"));
        Assert.Equal("Compact", (string?)card.Attribute("DisplayMode"));
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public void DailyPickScoreBadgeUsesTheSelectedVisualAccent()
    {
        var backgrounds = ReadDesktopPage("CurrentSeasonPage.xaml").Descendants()
            .Attributes("Background").Select(attribute => attribute.Value).ToArray();
        Assert.DoesNotContain("{ThemeResource SystemAccentColor}", backgrounds);
        Assert.Contains("{ThemeResource DesktopAccentBrush}", backgrounds);
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public void DetailCoverUsesTheCoverRadiusRatherThanTheControlRadius()
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var cover = Assert.Single(ReadDesktopPage("AnimeDetailPage.xaml").Descendants(),
            element => (string?)element.Attribute(xaml + "Name") == "CoverSection");
        Assert.Equal("{ThemeResource DesktopCoverRadius}", (string?)cover.Attribute("CornerRadius"));
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public void SavedDetailTagsUseTheSharedDesktopAccentBrushWhileTheOriginalHeadKeepsItsPath()
    {
        var source = File.ReadAllText(Path.Combine(FindRepository(), "Plugins", "AniMeido.Plugin.Base",
            "Views", "AnimeDetailPage.xaml.cs"));
        var tagMethod = source[source.IndexOf("private Border CreateTagButton", StringComparison.Ordinal)..];
        var desktopStart = tagMethod.IndexOf("#if UNO_DESKTOP", StringComparison.Ordinal);
        var originalStart = tagMethod.IndexOf("#else", desktopStart, StringComparison.Ordinal);
        Assert.True(originalStart > desktopStart);
        var desktop = tagMethod[desktopStart..originalStart];
        Assert.Contains("DesktopAccentBrush", desktop);
        Assert.Contains("DesktopCardBrush", desktop);
        Assert.Contains("DesktopTextBrush", desktop);
        Assert.DoesNotContain("SystemAccentColor", desktop);
        Assert.Contains("SystemAccentColor", tagMethod[originalStart..]);
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public void DynamicallyCreatedCharacterCardsUseTheDesktopSurfaceRoles()
    {
        var source = File.ReadAllText(Path.Combine(FindRepository(), "Plugins", "AniMeido.Plugin.Base",
            "Views", "AnimeDetailPage.xaml.cs"));
        Assert.Contains("avatarBorder.Background = (Brush)Application.Current.Resources[\"DesktopFieldBrush\"]", source);
        Assert.Contains("card.Background = (Brush)Application.Current.Resources[\"DesktopCardBrush\"]", source);
    }

    private static XDocument ReadDesktopPage(string file)
        => XDocument.Load(Path.Combine(FindRepository(), "Plugins", "AniMeido.Plugin.Base.Desktop", "Views", file));

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AniMeido.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("找不到 AniMeido.slnx。");
    }
}
