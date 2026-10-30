using AniMeido.Plugin.Player.Diagnostics;
using AniMeido.Plugin.Player.Sources.Web;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class PlayerRuntimeSettingsReadWarningTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private static readonly Uri Page = new("https://runtime.example.test/watch");
    private static readonly Uri Media = new("https://runtime.example.test/video.m3u8");

    [Theory]
    [InlineData("{broken-json", true)]
    [InlineData("null", true)]
    [InlineData("{\"GlobalTimeoutSeconds\":40}", false)]
    [InlineData(null, false)]
    public async Task ResolveReportsReadFallbackAndContinuesThroughTheRealHttpAndMediaProbe(
        string? original, bool shouldWarn)
    {
        using var paths = new MockAppDataPaths();
        var runtimePath = Path.Combine(paths.RootDirectory, "runtime.json");
        if (original is not null)
            await File.WriteAllTextAsync(runtimePath, original);
        using var handler = new MediaHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        await using var diagnostics = new PlaybackDiagnosticRecorder(paths.RootDirectory);
        var logger = new ReadLogger();
        await using var resolver = new WebMediaResolver(
            http,
            new PlayerRuntimeSettingsStore(runtimePath),
            new HostWebSessionManager(paths.RootDirectory),
            diagnostics,
            logger);

        var result = await resolver.ResolveAsync(Request(), CancellationToken.None).WaitAsync(Budget);

        Assert.Equal(Media, result.Uri);
        Assert.Equal([Page, Media], handler.Requests);
        if (shouldWarn)
        {
            var entry = Assert.Single(logger.Entries);
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.True(entry.Exception is JsonException or InvalidDataException);
            Assert.Contains("timeout", entry.Message, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Empty(logger.Entries);
        }
        if (original is null)
            Assert.False(File.Exists(runtimePath));
        else
            Assert.Equal(original, await File.ReadAllTextAsync(runtimePath));
        Assert.False(Directory.Exists(Path.Combine(paths.RootDirectory, "WebView2")));
    }

    [Fact]
    public async Task CancelledResolutionDoesNotReportRuntimeFallbackOrStartHttpWork()
    {
        using var paths = new MockAppDataPaths();
        var runtimePath = Path.Combine(paths.RootDirectory, "runtime.json");
        const string Original = "{broken-json";
        await File.WriteAllTextAsync(runtimePath, Original);
        using var handler = new MediaHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        await using var diagnostics = new PlaybackDiagnosticRecorder(paths.RootDirectory);
        var logger = new ReadLogger();
        await using var resolver = new WebMediaResolver(
            http,
            new PlayerRuntimeSettingsStore(runtimePath),
            new HostWebSessionManager(paths.RootDirectory),
            diagnostics,
            logger);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            resolver.ResolveAsync(Request(), cancellation.Token).WaitAsync(Budget));

        Assert.Empty(logger.Entries);
        Assert.Empty(handler.Requests);
        Assert.Equal(Original, await File.ReadAllTextAsync(runtimePath));
    }

    private static WebResolutionRequest Request() => new(
        "runtime-warning-fixture", Page, """https?://[^'"<> ]+\.m3u8""",
        false, null, true, false, new Dictionary<string, string>(), "fixture=1");

    private sealed class MediaHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(HttpMethod.Get, request.Method);
            Requests.Add(request.RequestUri!);
            if (request.RequestUri == Page)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"<video src='{Media}'></video>",
                        System.Text.Encoding.UTF8, "text/html"),
                    RequestMessage = new HttpRequestMessage(HttpMethod.Get, Page),
                });
            }

            Assert.Equal(Media, request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:4,\nsegment.ts"),
            });
        }
    }

    private sealed record ReadLogEntry(LogLevel Level, Exception? Exception, string Message);

    private sealed class ReadLogger : ILogger<WebMediaResolver>
    {
        public ConcurrentQueue<ReadLogEntry> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue(new ReadLogEntry(logLevel, exception, formatter(state, exception)));
    }
}
