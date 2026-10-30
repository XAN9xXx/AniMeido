using AniMeido.Plugin.Player.Sources.EasyBangumi;

namespace AniMeido.Tests;

public sealed class EasyHtmlObjectsTests
{
    private const string Html = """
        <html><head><title>Fixture</title></head><body>
        <div id="first"><a href="/one"><span>First</span><img src="/cover.png"></a></div>
        <div id="second"><a href="/two">Second</a></div>
        </body></html>
        """;

    [Fact]
    [Trait("Category", "Mechanism")]
    public void AdjacentQueriesReuseTreesWithoutRetainingEveryEarlierFragment()
    {
        var document = new EasyHtmlDocument(Html, null);
        var element = document.select("#first").first()!;
        var other = document.select("#second").first()!;

        Assert.Same(document.XPathRoot, document.XPathRoot);
        Assert.Same(element.XPathRoot, element.XPathRoot);
        Assert.Same(element.SelectionDocument, element.SelectionDocument);
        Assert.NotSame(document.XPathRoot, element.XPathRoot);
        Assert.NotSame(element.XPathRoot, other.XPathRoot);
        Assert.NotSame(element.SelectionDocument, other.SelectionDocument);

        var earlierCss = element.SelectionDocument;
        var earlierXPath = element.XPathRoot;
        _ = other.SelectionDocument;
        _ = other.XPathRoot;
        Assert.NotSame(earlierCss, element.SelectionDocument);
        Assert.NotSame(earlierXPath, element.XPathRoot);
    }

    [Fact]
    public void RepeatedCssAndXPathQueriesKeepTheirValuesAndBaseUrl()
    {
        var document = new EasyHtmlDocument(Html, "https://example.test/");
        var xpath = new EasyXPathFacade();
        for (var pass = 0; pass < 3; pass++)
        {
            Assert.Equal("Fixture", document.title());
            Assert.Equal("/one", document.select("a").first()!.attr("href"));
            Assert.Equal("/one", xpath.attr(document, "//a", "href"));
            Assert.Equal(2, xpath.nodes(document, "//a").size());
            Assert.Equal("https://example.test/", xpath.nodes(document, "//a").first()!.BaseUrl);
        }
    }

    [Fact]
    public void ElementCssQueriesKeepTheirExistingFragmentIncludingSelfSemantics()
    {
        var document = new EasyHtmlDocument(Html, null);
        var link = document.select("a").first()!;

        for (var pass = 0; pass < 3; pass++)
        {
            Assert.Equal(1, link.select("a").size());
            Assert.Equal("First", link.select("span").text());
            Assert.Equal("/cover.png", link.select("img").first()!.attr("src"));
        }
    }

    [Fact]
    public void XPathQueriesOnAnElementStayInsideItsSerializedFragment()
    {
        var document = new EasyHtmlDocument(Html, "https://example.test/");
        var xpath = new EasyXPathFacade();
        var element = document.select("#first").first()!;
        var xpathElement = xpath.nodes(document, "//div[@id='first']").first()!;

        foreach (var value in new[] { element, xpathElement })
        {
            for (var pass = 0; pass < 3; pass++)
            {
                Assert.Equal(1, xpath.nodes(value, "//a").size());
                Assert.Equal("/one", xpath.attr(value, "//a", "href"));
                Assert.Equal("First", xpath.text(value, "//span"));
                Assert.Equal("/cover.png", xpath.firstImage(value));
                Assert.Equal("https://example.test/", value.child(0)!.BaseUrl);
            }
        }
    }

    [Fact]
    public void DifferentDocumentsDoNotShareParsedContent()
    {
        var first = new EasyHtmlDocument("<a href='/one'>One</a>", null);
        var second = new EasyHtmlDocument("<a href='/two'>Two</a>", null);
        var xpath = new EasyXPathFacade();

        Assert.Equal("/one", xpath.attr(first, "//a", "href"));
        Assert.Equal("/two", xpath.attr(second, "//a", "href"));
        Assert.Equal("/one", xpath.attr(first, "//a", "href"));
    }
}
