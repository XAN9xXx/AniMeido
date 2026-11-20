using System.Xml.Linq;
using Xunit;

namespace AniMeido.Desktop.Tests;

[Trait("Category", "Mechanism")]
public sealed class DesktopPageSurfaceWiringTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void BroadcastWeekTilesUseTheNestedCardSurface()
        => AssertStyleBackground(Read("TodayPage.xaml"), "TodayTileStyle", "DesktopCardBrush");

    [Fact]
    public void CalendarGroupsAndTheirEntriesUseSeparateSurfaceRoles()
    {
        var page = Read("CurrentSeasonPage.xaml");
        AssertStyleBackground(page, "CalendarPanelStyle", "DesktopSurfaceBrush");
        AssertStyleBackground(page, "CalendarTileButtonStyle", "DesktopCardBrush");
        var day = FindKey(page, "DayTemplate");
        Assert.Contains(day.Descendants(), element => element.Name.LocalName == "Border"
            && (string?)element.Attribute("Background") == "{ThemeResource DesktopCardBrush}");
    }

    [Fact]
    public void RecommendationFeatureAndItsNestedRecordsUseSeparateSurfaceRoles()
    {
        var page = Read("RecommendationPage.xaml");
        AssertNamedBackground(page, "PreviewPanel", "DesktopSurfaceBrush");
        AssertNamedBackground(page, "TagPackBackdrop", "DesktopCardBrush");
        AssertNamedBackground(page, "DesktopRecommendationCover", "DesktopCardBrush");
        Assert.DoesNotContain(page.Descendants().Attributes("Background"), attribute =>
            attribute.Value.Contains("CardBackgroundFillColor", StringComparison.Ordinal)
            || attribute.Value.Contains("SolidBackgroundFillColorSecondary", StringComparison.Ordinal));
    }

    [Fact]
    public void AuxiliaryDirectoriesUseTheSecondaryNavigationSurface()
    {
        AssertNamedBackground(Read("ArchivePage.xaml"), "DesktopArchiveListPane", "DesktopNavigationBrush");
        AssertNamedBackground(Read("TagSearchResultPage.xaml"), "TagFilterPanel", "DesktopNavigationBrush");
    }

    [Fact]
    public void RecordRowsDetailedCardsAndScreenshotFramesUseTheCardSurface()
    {
        var mine = Read("ManagementPage.xaml");
        Assert.Equal("{ThemeResource DesktopCardBrush}", (string?)FindKey(mine, "MineRowTemplate")
            .Elements().Single().Attribute("Background"));
        Assert.Equal("{ThemeResource DesktopCardBrush}", (string?)FindKey(mine, "TagChipTemplate")
            .Elements().Single().Attribute("Background"));
        var detailed = FindKey(Read("PastSeasonPage.xaml"), "DesktopDetailedTemplate");
        Assert.Equal("AnimeCard", detailed.Elements().Single().Name.LocalName);
        Assert.Equal("Detailed", (string?)detailed.Elements().Single().Attribute("DisplayMode"));
        var archive = Read("ArchivePage.xaml");
        AssertNamedBackground(archive, "DesktopScreenshotPreview", "DesktopCardBrush");
        Assert.DoesNotContain(archive.Descendants().Attributes("Background"), attribute =>
            attribute.Value.Contains("CardBackgroundFillColor", StringComparison.Ordinal));
    }

    private static void AssertStyleBackground(XDocument page, string key, string brush)
        => Assert.Contains(FindKey(page, key).Elements(), element => (string?)element.Attribute("Property") == "Background"
            && (string?)element.Attribute("Value") == "{ThemeResource " + brush + "}");

    private static void AssertNamedBackground(XDocument page, string name, string brush)
        => Assert.Equal("{ThemeResource " + brush + "}", (string?)Assert.Single(page.Descendants(),
            element => (string?)element.Attribute(Xaml + "Name") == name).Attribute("Background"));

    private static XElement FindKey(XDocument page, string key)
        => Assert.Single(page.Descendants(), element => (string?)element.Attribute(Xaml + "Key") == key);

    private static XDocument Read(string file)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AniMeido.slnx")))
                return XDocument.Load(Path.Combine(directory.FullName, "Plugins", "AniMeido.Plugin.Base.Desktop", "Views", file));
        throw new DirectoryNotFoundException("找不到 AniMeido.slnx。");
    }
}
