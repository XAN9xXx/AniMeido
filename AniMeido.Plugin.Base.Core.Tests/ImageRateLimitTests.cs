using System.Net;
using System.Net.Http.Headers;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class ImageRateLimitTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 60)]
    public async Task FinalRateLimitSkipsTheSingleImageFailureCacheAndSharesTheTwoAttempts(bool ordinaryFirst, int cooldown)
    {
        using var paths = new MockAppDataPaths();
        var time = new ServiceTestTime();
        var calls = 0;
        using var http = new HttpClient(new Handler(_ => ++calls switch
        {
            1 => ordinaryFirst ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Limited(0),
            2 => Limited(cooldown),
            _ => Image(),
        }));
        var logger = new ServiceTestLogger<ImageDownloadCoordinator>();
        await using var images = new ImageDownloadCoordinator(paths.RootDirectory, http, retryDelay: TimeSpan.Zero, time: time, logger: logger);
        var key = ImageCacheKey.Cover(42);
        var result = await images.GetOrDownloadAsync(key, "https://img.example.test/42.jpg").WaitAsync(Budget);
        Assert.Equal(ImageDownloadStatus.RateLimited, result.Status);
        Assert.Equal(2, calls);
        time.Advance(TimeSpan.FromSeconds(cooldown));
        Assert.True((await images.GetOrDownloadAsync(key, "https://img.example.test/42.jpg").WaitAsync(Budget)).IsSuccess);
        Assert.Equal(3, calls);
        Assert.Equal(0, logger.Count(Microsoft.Extensions.Logging.LogLevel.Error));
        Assert.Empty(Directory.EnumerateFiles(paths.RootDirectory, "*.tmp.*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SharedCooldownBudgetExhaustionReturnsWithoutCreatingASingleImageFailure()
    {
        using var paths = new MockAppDataPaths();
        var time = new ServiceTestTime();
        var calls = 0;
        using var http = new HttpClient(new Handler(_ => ++calls == 1 ? Limited(60) : Image()));
        await using var images = new ImageDownloadCoordinator(paths.RootDirectory, http, retryDelay: TimeSpan.Zero, time: time);
        var result = await images.GetOrDownloadAsync(ImageCacheKey.Cover(1), "https://img.example.test/1.jpg");
        Assert.Equal(ImageDownloadStatus.RateLimited, result.Status);
        Assert.Equal(1, calls);
        var other = await images.GetOrDownloadAsync(ImageCacheKey.Cover(2), "https://img.example.test/2.jpg");
        Assert.Equal(ImageDownloadStatus.RateLimited, other.Status);
        Assert.Equal(1, calls);
        time.Advance(TimeSpan.FromSeconds(60));
        Assert.True((await images.GetOrDownloadAsync(ImageCacheKey.Cover(1), "https://img.example.test/1.jpg")).IsSuccess);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task BodyStalledAfterHeadersIsCancelledByEachFifteenSecondAttemptAndLeavesNoTemporaryFile()
    {
        using var paths = new MockAppDataPaths();
        var time = new ServiceTestTime();
        var entered = new SemaphoreSlim(0);
        var calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StalledContent(entered) };
        }));
        await using var images = new ImageDownloadCoordinator(paths.RootDirectory, http, retryDelay: TimeSpan.Zero, time: time);
        var reading = images.GetOrDownloadAsync(ImageCacheKey.Cover(42), "https://img.example.test/42.jpg");
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                Assert.True(await entered.WaitAsync(Budget));
                Assert.False(reading.IsCompleted);
                time.Advance(TimeSpan.FromSeconds(15));
            }
            Assert.Equal(ImageDownloadStatus.Failed, (await reading.WaitAsync(Budget)).Status);
            Assert.Equal(2, calls);
            Assert.Empty(Directory.EnumerateFiles(paths.RootDirectory, "*.tmp.*", SearchOption.AllDirectories));
        }
        finally
        {
            time.Advance(TimeSpan.FromDays(1));
            await ((Task)reading).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            entered.Dispose();
        }
    }

    private sealed class StalledContent : HttpContent
    {
        private readonly SemaphoreSlim _entered;
        internal StalledContent(SemaphoreSlim entered)
        {
            _entered = entered;
            Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        }
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new StalledStream(_entered));
    }

    private sealed class StalledStream(SemaphoreSlim entered) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            entered.Release();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private static HttpResponseMessage Limited(int seconds) => new(HttpStatusCode.TooManyRequests)
    {
        Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds)) },
    };
    private static HttpResponseMessage Image()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return response;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
