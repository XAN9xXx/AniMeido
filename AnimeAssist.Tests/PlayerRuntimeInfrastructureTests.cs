using AniMeido.Plugin.Player.Playback;
using AniMeido.Plugin.Player.Sources.Web;
using System.Net;

namespace AniMeido.Tests;

public sealed class PlayerRuntimeInfrastructureTests
{
    [Fact]
    public void HeaderNormalizer_LastCaseInsensitiveValueWins()
    {
        var headers = HeaderNormalizer.Merge(
            new Dictionary<string, string>
            {
                ["user-agent"] = "first",
                ["Referer"] = "https://first.test/",
            },
            new Dictionary<string, string>
            {
                ["User-Agent"] = "explicit",
            });

        Assert.Equal(2, headers.Count);
        Assert.Equal("explicit", headers["User-Agent"]);
        Assert.Equal("https://first.test/", headers["referer"]);
    }

    [Fact]
    public void HeaderNormalizer_RedactsSensitiveValuesAndUriQueries()
    {
        Assert.Equal(
            "<redacted>",
            HeaderNormalizer.Redact("Cookie", "session=secret"));
        Assert.Equal(
            "https://media.test/video.m3u8?<redacted>",
            HeaderNormalizer.Redact(
                "Url",
                "https://media.test/video.m3u8?token=secret"));
    }

    [Fact]
    public void LibMpvHeaderPlan_PreservesCommaInDedicatedUserAgent()
    {
        const string UserAgent =
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 "
            + "(KHTML, like Gecko) Chrome/124.0 Safari/537.36";
        var plan = LibMpvClient.CreateHttpHeaderPlan(
            new Dictionary<string, string>
            {
                ["User-Agent"] = UserAgent,
                ["Referer"] = "https://source.test/watch/1",
                ["Origin"] = "https://source.test",
                ["Cookie"] = "session=temporary",
            });

        Assert.Equal(UserAgent, plan.UserAgent);
        Assert.Equal("https://source.test/watch/1", plan.Referrer);
        Assert.Equal(
            [
                "Origin: https://source.test",
                "Cookie: session=temporary",
            ],
            plan.AdditionalFields);
    }

    [Fact]
    public async Task RuntimeSettings_UsesRequiredTimeoutPrecedence()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"animeido-runtime-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "runtime.json");
        try
        {
            var store = new PlayerRuntimeSettingsStore(path);
            var settings = PlayerRuntimeSettings.CreateDefault();
            settings.GlobalTimeoutSeconds = 40;
            settings.SourceTimeoutSeconds["source-a"] = 70;
            await store.WriteAsync(settings, CancellationToken.None);

            Assert.Equal(
                TimeSpan.FromSeconds(70),
                await store.GetEffectiveTimeoutAsync(
                    "source-a",
                    TimeSpan.FromSeconds(55),
                    CancellationToken.None));
            Assert.Equal(
                TimeSpan.FromSeconds(55),
                await store.GetEffectiveTimeoutAsync(
                    "source-b",
                    TimeSpan.FromSeconds(55),
                    CancellationToken.None));
            Assert.Equal(
                TimeSpan.FromSeconds(40),
                await store.GetEffectiveTimeoutAsync(
                    "source-c",
                    sourceDeclaredTimeout: null,
                    CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized,
        (int)SourceResolutionFailureKind.Authentication)]
    [InlineData(HttpStatusCode.Forbidden,
        (int)SourceResolutionFailureKind.AccessDenied)]
    [InlineData(HttpStatusCode.NotFound,
        (int)SourceResolutionFailureKind.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests,
        (int)SourceResolutionFailureKind.RateLimited)]
    public void AccessEvaluator_ClassifiesHttpFailures(
        HttpStatusCode status,
        int expected)
    {
        Assert.Equal(
            (SourceResolutionFailureKind)expected,
            WebPageAccessEvaluator.Classify(
                status,
                WebPageInteractionKind.None));
    }

    [Fact]
    public void HostSessionManager_NormalizesWwwHost()
    {
        Assert.Equal(
            "example.test",
            HostWebSessionManager.NormalizeHost("WWW.Example.Test."));
    }

    [Fact]
    public void WebResolver_ExtractsMediaUrlFromPlayerWrapperQuery()
    {
        const string Pattern =
            @"https?://[^\s""'<>\\]+?(?:\.m3u8|\.mp4)"
            + @"(?:\?[^\s""'<>\\]*)?";
        var wrapper = new Uri(
            "https://player.test/index.html"
            + "?url=https%3A%2F%2Fmedia.test%2Fepisode.m3u8"
            + "%3Ftoken%3Dtemporary");

        var found = WebMediaResolver.TryExtractEmbeddedMediaUri(
            wrapper,
            Pattern,
            wrapper,
            out var media);

        Assert.True(found);
        Assert.Equal("media.test", media.Host);
        Assert.Equal("/episode.m3u8", media.AbsolutePath);
        Assert.Equal("?token=temporary", media.Query);
    }

    [Fact]
    public void WebResolver_DoesNotTreatWrapperResponseAsLoadedMedia()
    {
        const string Pattern =
            @"https?://[^\s""'<>\\]+?(?:\.m3u8|\.mp4)"
            + @"(?:\?[^\s""'<>\\]*)?";
        var page = new Uri("https://source.test/watch/1");
        const string Wrapper =
            "https://player.test/index.html"
            + "?url=https%3A%2F%2Fmedia.test%2Fepisode.m3u8";

        Assert.False(WebMediaResolver.TryMatchObservedMediaUrl(
            Wrapper,
            Pattern,
            page,
            out _));
        Assert.True(WebMediaResolver.TryMatchObservedMediaUrl(
            "https://media.test/episode.m3u8?token=temporary",
            Pattern,
            page,
            out var media));
        Assert.Equal("media.test", media.Host);
    }

    [Theory]
    [InlineData("https://media.test/episode.m3u8", false)]
    [InlineData("https://media.test/episode.M3U8?token=temporary", false)]
    [InlineData("https://media.test/episode.mp4", true)]
    public void WebResolver_UsesRangeOnlyForNonHlsMedia(
        string url,
        bool expected)
    {
        Assert.Equal(
            expected,
            WebMediaResolver.ShouldUseRangeProbe(new Uri(url)));
    }

    [Fact]
    public void WebResolver_UsesCurrentPlayerDocumentAsMediaIdentity()
    {
        var headers = WebMediaResolver.BuildDocumentRequestHeaders(
            "https://player.test/embed/index.html?episode=1");

        Assert.Equal(
            "https://player.test/embed/index.html?episode=1",
            headers["Referer"]);
        Assert.Equal("https://player.test", headers["Origin"]);
    }

    [Theory]
    [InlineData(
        HttpStatusCode.Forbidden,
        "<p>The region has been denied.</p>",
        "媒体 CDN 拒绝当前网络地区")]
    [InlineData(
        HttpStatusCode.NotFound,
        "<h1>Not Found</h1>",
        "媒体地址已失效")]
    [InlineData(
        HttpStatusCode.Unauthorized,
        "",
        "可能需要重新登录源站")]
    public void WebResolver_ExplainsMediaRejection(
        HttpStatusCode status,
        string body,
        string expected)
    {
        Assert.Contains(
            expected,
            WebMediaResolver.DescribeMediaRejection(status, body));
    }

    [Fact]
    public void WebResolver_PrefersIframeForNestedPage()
    {
        const string Html =
            """
            <link rel="preconnect" href="https://cdn.test/">
            <iframe src="https://player.test/embed?id=42"></iframe>
            """;

        var nested = WebMediaResolver.FindNestedPageUri(
            Html,
            @"https?://[^\s""'<>\\]+",
            new Uri("https://source.test/watch/1"));

        Assert.Equal(
            "https://player.test/embed?id=42",
            nested?.AbsoluteUri);
    }
}
