using AniMeido.Plugin.Player.Diagnostics;
using AniMeido.Plugin.Player.Sources.Web;
using System.Net;

namespace AniMeido.Tests;

public sealed class MediaProbeTests
{
    private static readonly Uri Page = new("https://probe.example.test/watch");

    [Theory]
    [InlineData("video.mp4", "video/mp4", "bytes=0-1023")]
    [InlineData("playlist.m3u8", "application/vnd.apple.mpegurl", null)]
    public async Task ProbePreservesMediaIdentityAndHeaders(
        string path, string contentType, string? expectedRange)
    {
        using var paths = new MockAppDataPaths();
        await using var diagnostics = new PlaybackDiagnosticRecorder(paths.RootDirectory);
        using var handler = new ControlledHandler(request =>
        {
            Assert.Equal(expectedRange, request.Headers.Range?.ToString());
            Assert.Equal(Page, request.Headers.Referrer);
            Assert.Equal("fixture=1", Assert.Single(request.Headers.GetValues("Cookie")));
            Assert.Equal("fixture-agent", request.Headers.UserAgent.ToString());
            if (expectedRange is null)
                Assert.Contains(request.Headers.Accept, value => value.MediaType == "application/vnd.apple.mpegurl");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("fixture media", System.Text.Encoding.UTF8, contentType),
            };
        });
        using var http = new HttpClient(handler);
        var probe = new MediaProbe(http, diagnostics);
        var media = new WebResolvedMedia(new Uri(Page, path), new Dictionary<string, string>
        {
            ["Referer"] = Page.AbsoluteUri,
            ["Cookie"] = "fixture=1",
            ["User-Agent"] = "fixture-agent",
        });

        Assert.Same(media, await probe.ValidateAsync(media, Page, "fixture", default));
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "The region has been denied.", "媒体 CDN 拒绝当前网络地区")]
    [InlineData(HttpStatusCode.Unauthorized, "", "HTTP 401")]
    [InlineData(HttpStatusCode.NotFound, "", "HTTP 404")]
    public async Task ProbeReportsThePageAndExistingRejectionReason(
        HttpStatusCode status, string body, string expected)
    {
        using var paths = new MockAppDataPaths();
        await using var diagnostics = new PlaybackDiagnosticRecorder(paths.RootDirectory);
        using var handler = new ControlledHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body),
        });
        using var http = new HttpClient(handler);
        var probe = new MediaProbe(http, diagnostics);
        var media = new WebResolvedMedia(new Uri(Page, "video.mp4"), new Dictionary<string, string>());

        var error = await Assert.ThrowsAsync<SourceResolutionException>(() =>
            probe.ValidateAsync(media, Page, "fixture", default));
        Assert.Equal(SourceResolutionFailureKind.MediaRejected, error.Kind);
        Assert.Equal(Page, error.PageUri);
        Assert.Contains(expected, error.Message);
        Assert.Equal(1, handler.RequestCount);
    }

    private sealed class ControlledHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(respond(request));
        }
    }
}
