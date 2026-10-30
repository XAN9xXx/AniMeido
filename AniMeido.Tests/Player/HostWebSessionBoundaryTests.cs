using AniMeido.Plugin.Player.Diagnostics;
using AniMeido.Plugin.Player.Sources.Web;
using AniMeido.Plugin.Player.Views;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class HostWebSessionBoundaryTests
{
    private static readonly Uri Page = new("https://session.example.test/watch");
    private static readonly Uri Media = new("https://media.example.test/video.m3u8");
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private const string SourceId = "session-boundary-fixture";

    [Theory]
    [InlineData("{broken-json")]
    [InlineData("null")]
    public async Task VerifiedMediaStillUsesTheRealProbeWhenRecordingFails(string original)
    {
        await using var fixture = new Fixture();
        await fixture.SeedMetadataAsync(original);

        var result = await fixture.Resolver.CompleteVerificationAsync(
            Request(), ResolvedMedia(), TimeSpan.FromSeconds(10), CancellationToken.None).WaitAsync(Budget);

        Assert.Equal(Media, result.Uri);
        Assert.Equal("fixture-agent", result.Headers["User-Agent"]);
        Assert.Equal(Media, Assert.Single(fixture.Probe.Requests));
        var warning = Assert.Single(fixture.Logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.NotNull(warning.Exception);
        Assert.Contains(Page.Host, warning.Message);
        Assert.Equal(original, await File.ReadAllTextAsync(fixture.MetadataPath));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "WebView2")));
    }

    [Fact]
    public async Task VerifiedMediaRecordsTheSessionWithoutWarningWhenMetadataIsReadable()
    {
        await using var fixture = new Fixture();
        await fixture.SeedMetadataAsync("[]");

        var result = await fixture.Resolver.CompleteVerificationAsync(
            Request(), ResolvedMedia(), TimeSpan.FromSeconds(10), CancellationToken.None).WaitAsync(Budget);

        Assert.Equal(Media, result.Uri);
        Assert.Equal(Media, Assert.Single(fixture.Probe.Requests));
        Assert.Empty(fixture.Logger.Entries);
        var records = await fixture.Sessions.ListAsync(CancellationToken.None).WaitAsync(Budget);
        Assert.Null(records.ReadFailure);
        var saved = Assert.Single(records.Items);
        Assert.Equal(Page.Host, saved.Host);
        Assert.Equal(SourceId, Assert.Single(saved.SourceIds));
        Assert.Equal("fixture-agent", saved.UserAgent);
    }

    [Fact]
    public async Task CancelledVerificationDoesNotReportASaveFailureOrProbeMedia()
    {
        await using var fixture = new Fixture();
        await fixture.SeedMetadataAsync("[]");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Resolver.CompleteVerificationAsync(
                Request(), ResolvedMedia(), TimeSpan.FromSeconds(10), cancellation.Token).WaitAsync(Budget));

        Assert.Empty(fixture.Logger.Entries);
        Assert.Empty(fixture.Probe.Requests);
        Assert.Equal("[]", await File.ReadAllTextAsync(fixture.MetadataPath));
    }

    [Theory]
    [InlineData("{broken-json")]
    [InlineData("null")]
    public async Task SelectedSessionClearReportsMetadataFailureAndPreservesTheOriginalFile(string original)
    {
        await using var fixture = new Fixture();
        await fixture.SeedMetadataAsync(original);

        // 此入口只完成 Cookie 清理之后的记录保存，不执行原生 Cookie 操作。
        var result = await fixture.Resolver.CompleteSessionClearAsync(
            Page.Host, CancellationToken.None).WaitAsync(Budget);

        Assert.NotNull(result.MetadataFailure);
        Assert.Equal(original, await File.ReadAllTextAsync(fixture.MetadataPath));
        Assert.Empty(fixture.Probe.Requests);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "WebView2")));
    }

    [Theory]
    [InlineData("{broken-json")]
    [InlineData("null")]
    public async Task ExplicitAllSessionClearCanResetUnreadableMetadata(string original)
    {
        await using var fixture = new Fixture();
        await fixture.SeedMetadataAsync(original);

        var result = await fixture.Resolver.CompleteSessionClearAsync(
            host: null, CancellationToken.None).WaitAsync(Budget);

        Assert.Null(result.MetadataFailure);
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.MetadataPath));
        Assert.Equal(JsonValueKind.Array, saved.RootElement.ValueKind);
        Assert.Equal(0, saved.RootElement.GetArrayLength());
        Assert.Empty(fixture.Probe.Requests);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "WebView2")));
    }

    [Theory]
    [InlineData("session.example.test", false, "已清除 session.example.test 的登录会话。")]
    [InlineData(null, false, "已清除全部站点登录会话。")]
    [InlineData("session.example.test", true, "已清除 session.example.test 的登录 Cookie，但未能保存站点记录。原记录文件已保留。")]
    [InlineData(null, true, "已清除全部站点的登录 Cookie，但记录文件未能清空。请重试“清除全部会话”。")]
    public void ClearPresentationDistinguishesCompleteAndPartialResults(
        string? host, bool metadataFailed, string expected)
    {
        var result = new HostWebSessionClearResult(metadataFailed
            ? new IOException("internal-storage-fixture-detail")
            : null);

        var text = HostWebSessionPresentation.GetClearResultText(host, result);

        Assert.Equal(expected, text);
        Assert.DoesNotContain("internal-storage-fixture-detail", text);
    }

    [Fact]
    public void ReadFailureNoticeExplainsTheExplicitRecoveryAction()
    {
        Assert.Equal(
            "无法读取站点登录记录。可使用“清除全部会话”清除所有站点的登录信息，并恢复记录列表。",
            HostWebSessionPresentation.ReadFailureNotice);
    }

    private static WebResolutionRequest Request() => new(
        SourceId, Page, ".*", false, null, false, false,
        new Dictionary<string, string>(), Cookies: null);

    private static WebResolvedMedia ResolvedMedia() => new(Media,
        new Dictionary<string, string>
        {
            ["User-Agent"] = "fixture-agent",
            ["Referer"] = Page.AbsoluteUri,
        });

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly HttpClient _http;
        private readonly PlaybackDiagnosticRecorder _diagnostics;
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), $"AniMeido-host-session-boundary-{Guid.NewGuid():N}");
        public string MetadataPath => Path.Combine(Root, "Sessions", "metadata.json");
        public ProbeHandler Probe { get; } = new();
        public BoundaryLogger Logger { get; } = new();
        public HostWebSessionManager Sessions { get; }
        public WebMediaResolver Resolver { get; }

        public Fixture()
        {
            _http = new HttpClient(Probe);
            _diagnostics = new PlaybackDiagnosticRecorder(Root);
            Sessions = new HostWebSessionManager(Root);
            Resolver = new WebMediaResolver(
                _http,
                new PlayerRuntimeSettingsStore(Path.Combine(Root, "runtime.json")),
                Sessions,
                _diagnostics,
                Logger);
        }

        public async Task SeedMetadataAsync(string contents)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MetadataPath)!);
            await File.WriteAllTextAsync(MetadataPath, contents);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Resolver.DisposeAsync().AsTask().WaitAsync(Budget);
            }
            finally
            {
                _http.Dispose();
                await _diagnostics.DisposeAsync();
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
        }
    }

    private sealed class ProbeHandler : HttpMessageHandler
    {
        public ConcurrentQueue<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(Media, request.RequestUri);
            Requests.Enqueue(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                // text/plain 迫使真实 MediaProbe 读取 HLS 正文，而非只按 MIME 放行。
                Content = new StringContent("#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:4,\nsegment.ts"),
            });
        }
    }

    private sealed record BoundaryLogEntry(LogLevel Level, Exception? Exception, string Message);

    private sealed class BoundaryLogger : ILogger<WebMediaResolver>
    {
        public ConcurrentQueue<BoundaryLogEntry> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue(new BoundaryLogEntry(logLevel, exception, formatter(state, exception)));
    }
}
