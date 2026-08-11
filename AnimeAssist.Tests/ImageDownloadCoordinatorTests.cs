using System.Net;
using System.Net.Http.Headers;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class ImageDownloadCoordinatorTests
{
    [Fact]
    public async Task SameKey_CoalescesConcurrentRequests()
    {
        using var temp = new TempDirectory();
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelegateHandler(async (_, _, token) =>
        {
            await release.Task.WaitAsync(token);
            return ImageResponse();
        });
        var coordinator = CreateCoordinator(temp.Path, handler);
        var key = ImageCacheKey.Cover(42);

        var first = coordinator.GetOrDownloadAsync(key, TestUrl("42"));
        var second = coordinator.GetOrDownloadAsync(key, TestUrl("42"));
        release.SetResult();

        var results = await Task.WhenAll(first, second);
        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task CancellingOneSubscriber_DoesNotCancelSharedDownload()
    {
        using var temp = new TempDirectory();
        var requestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelegateHandler(async (_, _, token) =>
        {
            requestStarted.TrySetResult();
            await release.Task.WaitAsync(token);
            return ImageResponse();
        });
        var coordinator = CreateCoordinator(temp.Path, handler);
        using var cancellation = new CancellationTokenSource();
        var key = ImageCacheKey.Cover(7);

        var cancelled = coordinator.GetOrDownloadAsync(
            key,
            TestUrl("7"),
            cancellation.Token);
        var retained = coordinator.GetOrDownloadAsync(key, TestUrl("7"));
        await requestStarted.Task;
        cancellation.Cancel();
        var cancelledResult = await cancelled;
        release.SetResult();
        var retainedResult = await retained;

        Assert.Equal(ImageDownloadStatus.Cancelled, cancelledResult.Status);
        Assert.True(retainedResult.IsSuccess);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task LastSubscriberCancellation_ReleasesDownloadSlot()
    {
        using var temp = new TempDirectory();
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelegateHandler(async (call, _, token) =>
        {
            if (call == 1)
            {
                firstStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            secondStarted.SetResult();
            return ImageResponse();
        });
        var coordinator = CreateCoordinator(temp.Path, handler, maxConcurrency: 1);
        using var cancellation = new CancellationTokenSource();

        var obsolete = coordinator.GetOrDownloadAsync(
            ImageCacheKey.Cover(1),
            TestUrl("1"),
            cancellation.Token);
        await firstStarted.Task;
        var current = coordinator.GetOrDownloadAsync(
            ImageCacheKey.Cover(2),
            TestUrl("2"));
        cancellation.Cancel();

        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ImageDownloadStatus.Cancelled, (await obsolete).Status);
        Assert.True((await current).IsSuccess);
    }

    [Fact]
    public async Task TransientFailure_RetriesOnce()
    {
        using var temp = new TempDirectory();
        var handler = new DelegateHandler((call, _, _) => Task.FromResult(
            call == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : ImageResponse()));
        var coordinator = CreateCoordinator(temp.Path, handler);

        var result = await coordinator.GetOrDownloadAsync(
            ImageCacheKey.Cover(3),
            TestUrl("3"));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task TerminalFailure_UsesCooldownWithoutRetrying()
    {
        using var temp = new TempDirectory();
        var handler = new DelegateHandler((_, _, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.NotFound)));
        var coordinator = CreateCoordinator(temp.Path, handler);
        var key = ImageCacheKey.Cover(4);

        var first = await coordinator.GetOrDownloadAsync(key, TestUrl("4"));
        var second = await coordinator.GetOrDownloadAsync(key, TestUrl("4"));

        Assert.Equal(ImageDownloadStatus.Failed, first.Status);
        Assert.Equal(ImageDownloadStatus.Failed, second.Status);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task InvalidContentType_IsTerminalAndDoesNotRetry()
    {
        using var temp = new TempDirectory();
        var handler = new DelegateHandler((_, _, _) =>
        {
            var content = new StringContent("not an image");
            content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            });
        });
        var coordinator = CreateCoordinator(temp.Path, handler);

        var result = await coordinator.GetOrDownloadAsync(
            ImageCacheKey.Cover(40),
            TestUrl("40"));

        Assert.Equal(ImageDownloadStatus.Failed, result.Status);
        Assert.Equal(1, handler.CallCount);
        Assert.False(coordinator.HasLocalCache(ImageCacheKey.Cover(40)));
    }

    [Fact]
    public async Task OversizedContentLength_IsTerminalAndDoesNotCreateCache()
    {
        using var temp = new TempDirectory();
        var handler = new DelegateHandler((_, _, _) =>
        {
            var response = ImageResponse();
            response.Content.Headers.ContentLength = (5 * 1024 * 1024) + 1;
            return Task.FromResult(response);
        });
        var coordinator = CreateCoordinator(temp.Path, handler);
        var key = ImageCacheKey.Cover(43);

        var result = await coordinator.GetOrDownloadAsync(key, TestUrl("43"));

        Assert.Equal(ImageDownloadStatus.Failed, result.Status);
        Assert.Equal(1, handler.CallCount);
        Assert.False(coordinator.HasLocalCache(key));
    }

    [Fact]
    public async Task ClearAll_CancelsDownloadAndPreventsLateWriteBack()
    {
        using var temp = new TempDirectory();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelegateHandler(async (_, _, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return ImageResponse();
        });
        var coordinator = CreateCoordinator(temp.Path, handler);
        var key = ImageCacheKey.Cover(41);

        var download = coordinator.GetOrDownloadAsync(key, TestUrl("41"));
        await started.Task;
        coordinator.ClearAll();
        var result = await download.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ImageDownloadStatus.Cancelled, result.Status);
        Assert.False(coordinator.HasLocalCache(key));
    }

    [Fact]
    public async Task Invalidate_RemovesCorruptCacheAndDownloadsAgain()
    {
        using var temp = new TempDirectory();
        var handler = new DelegateHandler((_, _, _) => Task.FromResult(ImageResponse()));
        var coordinator = CreateCoordinator(temp.Path, handler);
        var key = ImageCacheKey.Cover(5);
        await File.WriteAllTextAsync(coordinator.GetLocalPath(key), "broken");

        coordinator.Invalidate(key);
        var result = await coordinator.GetOrDownloadAsync(key, TestUrl("5"));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, handler.CallCount);
        Assert.True(new FileInfo(coordinator.GetLocalPath(key)).Length > "broken".Length);
    }

    [Fact]
    public void AvatarCacheKey_IsStableAndSeparateFromCoverCache()
    {
        using var temp = new TempDirectory();
        var coordinator = CreateCoordinator(temp.Path, new DelegateHandler(
            (_, _, _) => Task.FromResult(ImageResponse())));

        var first = coordinator.GetLocalPath(ImageCacheKey.Avatar(TestUrl("avatar")));
        var second = coordinator.GetLocalPath(ImageCacheKey.Avatar(TestUrl("avatar")));
        var cover = coordinator.GetLocalPath(ImageCacheKey.Cover(1));

        Assert.Equal(first, second);
        Assert.NotEqual(first, cover);
        Assert.Contains($"{Path.DirectorySeparatorChar}avatars{Path.DirectorySeparatorChar}", first);
    }

    [Theory]
    [InlineData(150, 0, 2, 300)]
    [InlineData(260, 260, 1.5, 390)]
    [InlineData(300, 325.2, 1.25, 407)]
    [InlineData(64, 64, 0, 64)]
    public void DecodeWidth_UsesActualWidthAndDpi(
        double logicalWidth,
        double actualWidth,
        double scale,
        int expected)
        => Assert.Equal(
            expected,
            ManagedImageLoader.CalculateDecodePixelWidth(
                logicalWidth,
                actualWidth,
                scale));

    private static ImageDownloadCoordinator CreateCoordinator(
        string root,
        HttpMessageHandler handler,
        int maxConcurrency = 4)
        => new(
            root,
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            maxConcurrency,
            retryDelay: TimeSpan.Zero,
            failureCooldown: TimeSpan.FromMinutes(1),
            uriValidator: _ => true);

    private static string TestUrl(string id) => $"https://images.test/{id}.jpg";

    private static HttpResponseMessage ImageResponse()
    {
        var content = new ByteArrayContent([1, 2, 3, 4, 5, 6, 7, 8]);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class DelegateHandler(
        Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback)
        : HttpMessageHandler
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => callback(Interlocked.Increment(ref _callCount), request, cancellationToken);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"AniMeido-image-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
