using AniMeido.Plugin.Player.Diagnostics;
using AniMeido.Plugin.Player.Sources.Web;
using System.Net;
using System.Net.Http.Headers;

namespace AniMeido.Tests;

public sealed class WebMediaResolverShortReadTests
{
    private static readonly Uri Page = new("https://short-read.example.test/page");
    private static readonly Uri Media = new("https://short-read.example.test/video.mp4");

    [Fact]
    public async Task Resolve_Mp4WithPlainTextMimeAndThreeByteReadsIsAccepted()
    {
        // 完整 24 字节 ftyp box；本测试只验证容器签名嗅探，不启动播放。
        byte[] header = [0, 0, 0, 24, 102, 116, 121, 112, 105, 115, 111, 109,
            0, 0, 0, 0, 105, 115, 111, 109, 109, 112, 52, 50];
        await using var stream = new ThreeByteReadStream(header);
        using var handler = new ControlledHandler(stream);
        using var http = new HttpClient(handler);
        var root = Path.Combine(Path.GetTempPath(), $"AniMeido-web-short-read-{Guid.NewGuid():N}");
        var diagnostics = new PlaybackDiagnosticRecorder(root);
        using var resolver = new WebMediaResolver(http,
            new PlayerRuntimeSettingsStore(Path.Combine(root, "runtime.json")),
            new HostWebSessionManager(root), diagnostics);
        try
        {
            var request = new WebResolutionRequest("short-read-fixture", Page,
                """(?<v>https?://[^'"<> ]+\.mp4)""", false, null, true, true,
                new Dictionary<string, string>(), "fixture=1", SourceDeclaredTimeout: TimeSpan.FromSeconds(10));

            var result = await resolver.ResolveAsync(request, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(Media, result.Uri);
            Assert.Equal(2, handler.RequestCount);
            Assert.Equal("bytes=0-1023", handler.Range);
            Assert.True(stream.ReadCount >= 6);
            Assert.InRange(stream.BytesRead, 16, header.Length);
            Assert.InRange(stream.MaxReadSize, 1, 3);
        }
        finally
        {
            await diagnostics.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ControlledHandler(Stream stream) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? Range { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (request.RequestUri == Page)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"<video src='{Media}'></video>", System.Text.Encoding.UTF8, "text/html"),
                    RequestMessage = request,
                });
            }
            Assert.Equal(Media, request.RequestUri);
            Range = request.Headers.Range?.ToString();
            var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request });
        }
    }

    private sealed class ThreeByteReadStream(byte[] bytes) : Stream
    {
        public int BytesRead { get; private set; }
        public int ReadCount { get; private set; }
        public int MaxReadSize { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var length = Math.Min(3, Math.Min(buffer.Length, bytes.Length - BytesRead));
            bytes.AsSpan(BytesRead, length).CopyTo(buffer);
            BytesRead += length;
            ReadCount++;
            MaxReadSize = Math.Max(MaxReadSize, length);
            return length;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
