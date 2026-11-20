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
        var source = ReadCard();
        Assert.Equal("{ThemeResource DesktopCardBrush}", (string?)FindPart("CardSurface").Attribute("Background"));
        Assert.Equal("Transparent", (string?)FindPart("CardRoot").Attribute("Background"));
        Assert.Contains("CardSurface.Background = new SolidColorBrush", source);
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
            Assert.Equal("{x:Bind Anime}", (string?)card.Attribute("Anime"));
            Assert.Null(card.Attribute("DataContext"));
            Assert.Equal("{x:Bind Status}", (string?)card.Attribute("TrackingStatus"));
            Assert.Equal("False", (string?)card.Attribute("ShowQuickActions"));
            Assert.Equal("OnAnimeCardClicked", (string?)card.Attribute("CardClicked"));
        }
    }

    [Fact]
    public void DetailedCardKeepsBoundTextAndSevenReadableLinesWithoutAPixelHeightClamp()
    {
        var source = ReadCard();
        Assert.Contains("TitleText.Text = DetailedTitle.Text = anime?.Title", source);
        Assert.Contains("DescriptionText.Text = anime?.Description", source);
        Assert.Equal("7", (string?)FindPart("DescriptionText").Attribute("MaxLines"));
        Assert.Null(FindPart("DescriptionText").Attribute("MaxHeight"));
        Assert.Contains("CoverHost.Height = detailed ? double.NaN", source);
        Assert.Contains("CoverHost.SizeChanged", source);
        Assert.Equal("17", (string?)FindPart("DetailedTitle").Attribute("FontSize"));
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
        Assert.Equal("OnShelfPointerWheelChanged", (string?)scroller.Attribute("PointerWheelChanged"));
    }

    private static string Read(string path)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AniMeido.slnx")))
                return File.ReadAllText(Path.Combine(directory.FullName, path));
        throw new DirectoryNotFoundException("找不到 AniMeido.slnx。");
    }
}
