using Xunit;

using System.Xml.Linq;

namespace AniMeido.Desktop.Tests;

[Trait("Category", "Mechanism")]
public sealed class DesktopAnimeCardWiringTests
{
    [Fact]
    public void DesktopPostersFillTheirFixedFrameForEverySourceAspectRatio()
    {
        var cover = FindPart("CoverImage");
        Assert.Equal("UniformToFill", (string?)cover.Attribute("Stretch"));
        Assert.Equal("Stretch", (string?)cover.Attribute("HorizontalAlignment"));
        Assert.Equal("Stretch", (string?)cover.Attribute("VerticalAlignment"));
    }

    [Fact]
    public void ScoreRefreshReassignsBothItsForegroundAndItsDarkFallbackSurface()
    {
        var source = ReadCard();
        Assert.Contains("ScoreText.Foreground", source);
        Assert.Contains("DesktopAnimeCardPresentation.ScoreForegroundResource", source);
        Assert.Contains("new SolidColorBrush(Colors.Black)", source);
    }

    [Fact]
    public void CoverRetryUsesTheCurrentDesktopAccent()
    {
        var assignments = ReadCard().Split('\n').Where(line => line.Contains("RetryRing.Foreground", StringComparison.Ordinal));
        Assert.Contains("DesktopAccentBrush", Assert.Single(assignments));
    }

    private static string ReadCard()
        => Read("Plugins/AniMeido.Plugin.Base.Desktop/Views/Controls/AnimeCard.xaml.cs");

    [Fact]
    public void PosterAndDetailedCardsKeepAContinuousSurfaceIncludingCaptionSpacing()
    {
        var root = FindPart("CardRoot");
        Assert.Equal("Transparent", (string?)root.Attribute("Background"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var surface = Assert.Single(root.Elements(), element => (string?)element.Attribute(x + "Name") == "CardSurface");
        foreach (var name in new[] { "CoverHost", "CaptionHost", "DetailedHost" })
            Assert.Single(surface.Descendants(), element => (string?)element.Attribute(x + "Name") == name);
        Assert.Equal("False", (string?)FindPart("CoverEdge").Attribute("IsHitTestVisible"));
        Assert.Equal("False", (string?)FindPart("CoverSelection").Attribute("IsHitTestVisible"));
    }

    [Fact]
    public void CatalogUsesTheWholeSharedCardForBothPresentationsWithoutQuickActions()
    {
        var document = XDocument.Parse(Read("Plugins/AniMeido.Plugin.Base.Desktop/Views/PastSeasonPage.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var (key, presentation) in new[] { ("DesktopPosterTemplate", "Poster"), ("DesktopDetailedTemplate", "Detailed") })
        {
            var template = Assert.Single(document.Descendants(), element => (string?)element.Attribute(x + "Key") == key);
            var card = Assert.Single(template.Elements());
            Assert.Equal("AnimeCard", card.Name.LocalName);
            Assert.Equal(presentation, (string?)card.Attribute("DisplayMode"));
            Assert.Equal(presentation == "Detailed" ? "{Binding Anime, Mode=OneWay}" : "{x:Bind Anime}", (string?)card.Attribute("Anime"));
            Assert.Null(card.Attribute("DataContext"));
            Assert.Equal(presentation == "Detailed" ? "{Binding Status, Mode=OneWay}" : "{x:Bind Status}", (string?)card.Attribute("TrackingStatus"));
            Assert.Equal("False", (string?)card.Attribute("ShowQuickActions"));
            Assert.Equal("OnAnimeCardClicked", (string?)card.Attribute("CardClicked"));
        }
    }

    [Fact]
    public void DetailedCardKeepsBoundTextAndFourReadableLinesWithoutAPixelHeightClamp()
    {
        var source = ReadCard();
        Assert.Contains("TitleText.Text = DetailedTitle.Text = anime?.Title", source);
        Assert.Contains("DescriptionText.Text = anime?.Description", source);
        Assert.Equal("4", (string?)FindPart("DescriptionText").Attribute("MaxLines"));
        Assert.Null(FindPart("DescriptionText").Attribute("MaxHeight"));
        Assert.Null(FindPart("DescriptionText").Attribute("Height"));
        Assert.Equal("16", (string?)FindPart("DetailedTitle").Attribute("FontSize"));
    }

    [Fact]
    public void ComponentHasAnExplicitAnimeInputAndDoesNotReplaceItsParentsDataContext()
    {
        Assert.Contains("AnimeProperty = DependencyProperty.Register", ReadCard());
        Assert.DoesNotContain("DataContext", ReadCard());
        var document = XDocument.Parse(Read("Plugins/AniMeido.Plugin.Base.Desktop/Views/Controls/AnimeCard.xaml"));
        Assert.DoesNotContain(document.Descendants().Attributes(), attribute => attribute.Value.Contains("{Binding", StringComparison.Ordinal));
    }

    private static XElement FindPart(string name)
    {
        var document = XDocument.Parse(Read("Plugins/AniMeido.Plugin.Base.Desktop/Views/Controls/AnimeCard.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return Assert.Single(document.Descendants(), element => (string?)element.Attribute(x + "Name") == name);
    }

    [Fact]
    public void CalendarKeepsHorizontalScrollingWithoutTheRemovedPager()
    {
        var document = XDocument.Parse(Read("Plugins/AniMeido.Plugin.Base.Desktop/Views/CurrentSeasonPage.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        Assert.DoesNotContain(document.Descendants(), element => ((string?)element.Attribute(x + "Name"))?.StartsWith("DesktopShelf", StringComparison.Ordinal) == true);
        var scroller = Assert.Single(document.Descendants(), element => (string?)element.Attribute(x + "Name") == "ShelfScroller");
        Assert.Equal("Enabled", (string?)scroller.Attribute("HorizontalScrollMode"));
        Assert.Equal("DesktopWheelShelf", scroller.Name.LocalName);
        Assert.Null(scroller.Attribute("PointerWheelChanged"));
    }

    private static string Read(string path)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AniMeido.slnx")))
                return File.ReadAllText(Path.Combine(directory.FullName, path));
        throw new DirectoryNotFoundException("找不到 AniMeido.slnx。");
    }
}
