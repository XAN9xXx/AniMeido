using AniMeido.Plugin.Player.Sources.Web;
using Jint;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace AniMeido.Tests;

public sealed class WebMediaResolverCaptureTests(ITestOutputHelper output)
{
    [Fact]
    public void Capture_ReturnsInitialDomAndPerformanceCandidates()
    {
        var browser = CreateBrowser();
        browser.Execute("""
            domNodes.push({ currentSrc: "https://media.example.test/dom.mp4" });
            resources.push({ name: "https://media.example.test/resource.m3u8" });
            """);

        browser.Execute(WebMediaCaptureScripts.Install);
        var candidates = ReadCandidates(browser);

        Assert.Contains(candidates, candidate => candidate.Url == "https://media.example.test/dom.mp4");
        Assert.Contains(candidates, candidate => candidate.Url == "https://media.example.test/resource.m3u8");
        Assert.All(candidates, candidate => Assert.False(candidate.Confirmed));
    }

    [Fact]
    public void Capture_ReinstallDoesNotWrapXhrTwice_AndPollingDrainsQueuedCandidates()
    {
        var browser = CreateBrowser();
        browser.Execute(WebMediaCaptureScripts.Install);
        browser.Execute("const installedOpen = XMLHttpRequest.prototype.open;");

        browser.Execute(WebMediaCaptureScripts.Install);
        browser.Execute("""
            window.__animeidoMediaCandidates.push({
                url: "https://media.example.test/confirmed",
                confirmed: true
            });
            """);
        var candidate = Assert.Single(ReadCandidates(browser));

        Assert.Equal("https://media.example.test/confirmed", candidate.Url);
        Assert.True(candidate.Confirmed);
        Assert.True(browser.Evaluate("XMLHttpRequest.prototype.open === installedOpen").AsBoolean());
        Assert.Empty(ReadCandidates(browser));
    }

    [Fact]
    public void Capture_OnlyReturnsNewCandidatesWhenDomAndResourcesRepeat()
    {
        var browser = CreateBrowser();
        browser.Execute("""
            domNodes.push({ currentSrc: "https://media.example.test/dom.mp4" });
            resources.push({ name: "https://media.example.test/resource.m3u8" });
            """);
        browser.Execute(WebMediaCaptureScripts.Install);

        Assert.Equal(new[] { "https://media.example.test/dom.mp4", "https://media.example.test/resource.m3u8" },
            ReadCandidates(browser).Select(candidate => candidate.Url));
        Assert.Empty(ReadCandidates(browser));

        browser.Execute("""
            domNodes.push({ currentSrc: "https://media.example.test/new-dom.mp4" });
            resources.push({ name: "https://media.example.test/new-dom.mp4" });
            resources.push({ name: "https://media.example.test/new-resource.m3u8" });
            """);
        Assert.Equal(new[] { "https://media.example.test/new-dom.mp4", "https://media.example.test/new-resource.m3u8" },
            ReadCandidates(browser).Select(candidate => candidate.Url));
        Assert.Empty(ReadCandidates(browser));
    }

    [Fact]
    public void Capture_ConfirmedCandidateCanUpgradePreviouslyUnconfirmedUrl()
    {
        var browser = CreateBrowser();
        browser.Execute("resources.push({ name: 'https://media.example.test/no-extension' });");
        browser.Execute(WebMediaCaptureScripts.Install);
        Assert.False(Assert.Single(ReadCandidates(browser)).Confirmed);

        browser.Execute("""
            window.__animeidoMediaCandidates.push({
                url: "https://media.example.test/no-extension",
                confirmed: true
            });
            """);
        var upgraded = Assert.Single(ReadCandidates(browser));

        Assert.Equal("https://media.example.test/no-extension", upgraded.Url);
        Assert.True(upgraded.Confirmed);
        Assert.Empty(ReadCandidates(browser));
    }

    [Fact]
    public void Capture_ResourceBufferReplacementDoesNotHideNewEntries()
    {
        var browser = CreateBrowser();
        browser.Execute("resources.push({ name: 'https://media.example.test/first.js' });");
        browser.Execute(WebMediaCaptureScripts.Install);
        Assert.Equal("https://media.example.test/first.js", Assert.Single(ReadCandidates(browser)).Url);

        browser.Execute("resources.splice(0, resources.length, { name: 'https://media.example.test/second.js' });");

        Assert.Equal("https://media.example.test/second.js", Assert.Single(ReadCandidates(browser)).Url);
        Assert.Empty(ReadCandidates(browser));
    }

    [Fact]
    public void Capture_NewDocumentStartsWithItsOwnCandidateHistory()
    {
        var browser = CreateBrowser();
        browser.Execute("resources.push({ name: 'https://media.example.test/shared.m3u8' });");
        browser.Execute(WebMediaCaptureScripts.Install);
        Assert.Single(ReadCandidates(browser));

        browser.Execute("""
            document = {
                readyState: "complete",
                documentElement: {},
                querySelectorAll: () => domNodes
            };
            """);

        Assert.Equal("https://media.example.test/shared.m3u8", Assert.Single(ReadCandidates(browser)).Url);
        Assert.Empty(ReadCandidates(browser));
    }

    [Fact]
    public void Capture_ReinstallStartsANewCandidateHistoryWithoutWrappingXhrAgain()
    {
        var browser = CreateBrowser();
        browser.Execute("resources.push({ name: 'https://media.example.test/reinstall.m3u8' });");
        browser.Execute(WebMediaCaptureScripts.Install);
        browser.Execute("const installedOpen = XMLHttpRequest.prototype.open;");
        Assert.Single(ReadCandidates(browser));

        browser.Execute(WebMediaCaptureScripts.Install);

        Assert.Equal("https://media.example.test/reinstall.m3u8", Assert.Single(ReadCandidates(browser)).Url);
        Assert.True(browser.Evaluate("XMLHttpRequest.prototype.open === installedOpen").AsBoolean());
        Assert.Empty(ReadCandidates(browser));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(10000)]
    [Trait("Category", "Performance")]
    public void Capture_ReportsFirstAndRepeatedPollJsonBytesWithoutDroppingResources(int count)
    {
        var browser = CreateBrowser();
        var resources = Enumerable.Range(0, count)
            .Select(index => new { name = $"https://resources.example.test/resource-{index:D5}.js" });
        browser.Execute($"resources.push(...{JsonSerializer.Serialize(resources)});");
        browser.Execute(WebMediaCaptureScripts.Install);
        var firstJson = ReadCandidatesJson(browser);

        Assert.Equal(count, JsonSerializer.Deserialize<WebCapturedUrl[]>(firstJson)!.Length);
        var repeatedBytes = 0;
        for (var poll = 0; poll < 10; poll++)
        {
            repeatedBytes += Encoding.UTF8.GetByteCount(ReadCandidatesJson(browser));
        }

        output.WriteLine("{0} resources: first JSON {1:N0} UTF-8 B; 10 unchanged polls {2:N0} UTF-8 B",
            count, Encoding.UTF8.GetByteCount(firstJson), repeatedBytes);
    }

    [Theory]
    [InlineData("image/png", "https://assets.example.test/cover.png")]
    [InlineData("application/json", "https://api.example.test/data")]
    [InlineData("video/mp4", "https://media.example.test/video.mp4")]
    [InlineData("", "https://api.example.test/no-extension")]
    [Trait("Category", "Mechanism")]
    public void Capture_FetchDoesNotCloneUninspectedResponses(string contentType, string responseUrl)
    {
        const string Body = "https://media.example.test/not-inspected.m3u8";
        var browser = CreateBrowserWithFetch(contentType, responseUrl, Body);
        browser.Execute(WebMediaCaptureScripts.Install);

        Assert.True(FetchReturnsOriginalResponse(browser));
        Assert.Equal(0, browser.Evaluate("cloneCalls").AsNumber());
        Assert.Equal(0, browser.Evaluate("cloneReadCalls").AsNumber());
        AssertOriginalBodyRemainsReadable(browser, Body);
        Assert.Empty(ReadCandidates(browser));
    }

    [Theory]
    [InlineData("application/vnd.apple.mpegurl", "https://media.example.test/playlist")]
    [InlineData("text/plain; charset=utf-8", "https://media.example.test/playlist")]
    [InlineData("application/octet-stream", "https://media.example.test/playlist")]
    [InlineData("application/json", "https://media.example.test/playlist.M3U8?token=temporary")]
    [InlineData("", "https://media.example.test/playlist.m3u8")]
    [Trait("Category", "Mechanism")]
    public void Capture_FetchInspectsEligibleResponseOnceWithoutConsumingOriginalBody(
        string contentType,
        string responseUrl)
    {
        const string TailUrl = "https://media.example.test/complete-body-tail.mp4?token=temporary";
        var body = "  #EXTM3U\n#" + new string('x', 96 * 1024) + "\n" + TailUrl;
        var browser = CreateBrowserWithFetch(contentType, responseUrl, body);
        browser.Execute(WebMediaCaptureScripts.Install);

        Assert.True(FetchReturnsOriginalResponse(browser));
        Assert.Equal(1, browser.Evaluate("cloneCalls").AsNumber());
        Assert.Equal(1, browser.Evaluate("cloneReadCalls").AsNumber());
        var candidates = ReadCandidates(browser);

        Assert.Collection(candidates,
            candidate =>
            {
                Assert.Equal(responseUrl, candidate.Url);
                Assert.True(candidate.Confirmed);
            },
            candidate =>
            {
                Assert.Equal(TailUrl, candidate.Url);
                Assert.False(candidate.Confirmed);
            });
        Assert.Empty(ReadCandidates(browser));
        AssertOriginalBodyRemainsReadable(browser, body);
    }

    private static Engine CreateBrowserWithFetch(string contentType, string responseUrl, string body)
    {
        var browser = CreateBrowser();
        browser.SetValue("fetchContentType", contentType);
        browser.SetValue("fetchResponseUrl", responseUrl);
        browser.SetValue("fetchBody", body);
        browser.Execute("""
            let cloneCalls = 0;
            let cloneReadCalls = 0;
            let originalReadCalls = 0;
            const responseHeaders = { get: () => fetchContentType };
            const originalResponse = {
                url: fetchResponseUrl,
                headers: responseHeaders,
                bodyUsed: false,
                text() {
                    originalReadCalls++;
                    this.bodyUsed = true;
                    return Promise.resolve(fetchBody);
                },
                clone() {
                    cloneCalls++;
                    return {
                        url: this.url,
                        headers: this.headers,
                        text() {
                            cloneReadCalls++;
                            return Promise.resolve(fetchBody);
                        }
                    };
                }
            };
            window.fetch = async () => originalResponse;
            """);
        return browser;
    }

    private static bool FetchReturnsOriginalResponse(Engine browser)
        => browser.Evaluate("""
            (async () => {
                const response = await window.fetch(fetchResponseUrl);
                await Promise.resolve();
                return response === originalResponse;
            })()
            """).UnwrapIfPromise(TimeSpan.FromSeconds(5)).AsBoolean();

    private static void AssertOriginalBodyRemainsReadable(Engine browser, string expectedBody)
    {
        Assert.Equal(0, browser.Evaluate("originalReadCalls").AsNumber());
        Assert.False(browser.Evaluate("originalResponse.bodyUsed").AsBoolean());
        var body = browser.Evaluate("originalResponse.text()")
            .UnwrapIfPromise(TimeSpan.FromSeconds(5)).AsString();

        Assert.Equal(expectedBody, body);
        Assert.Equal(1, browser.Evaluate("originalReadCalls").AsNumber());
    }

    private static Engine CreateBrowser()
    {
        var browser = new Engine();
        browser.Execute("""
            const domNodes = [];
            const resources = [];
            let document = {
                readyState: "complete",
                documentElement: {},
                querySelectorAll: () => domNodes
            };
            const performance = { getEntriesByType: () => resources };
            const window = {};
            function MutationObserver() { this.observe = () => {}; }
            function XMLHttpRequest() {}
            XMLHttpRequest.prototype.open = function() {};
            XMLHttpRequest.prototype.send = function() {};
            """);
        return browser;
    }

    private static WebCapturedUrl[] ReadCandidates(Engine browser)
        => JsonSerializer.Deserialize<WebCapturedUrl[]>(
            ReadCandidatesJson(browser)) ?? [];

    private static string ReadCandidatesJson(Engine browser)
        => browser.Evaluate($"JSON.stringify({WebMediaCaptureScripts.Poll})").AsString();
}
