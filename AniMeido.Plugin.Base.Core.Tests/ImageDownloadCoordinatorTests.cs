using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class ImageDownloadCoordinatorTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Mechanism")]
    public async Task RequestingAnotherImageReleasesExpiredFailureRecords(bool alreadyCached)
    {
        using var temp = new TempDirectory();
        using var handler = new DelegateHandler((_, request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath == "/current.jpg"
                ? ImageResponse()
                : new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var http = new HttpClient(handler);
        await using var coordinator = new ImageDownloadCoordinator(
            temp.Path, http, retryDelay: TimeSpan.Zero, failureCooldown: TimeSpan.Zero);
        if (alreadyCached)
            Assert.True((await coordinator.GetOrDownloadAsync(ImageCacheKey.Cover(21), TestUrl("current"))).IsSuccess);
        for (var id = 1; id <= 20; id++)
        {
            var result = await coordinator.GetOrDownloadAsync(ImageCacheKey.Cover(id), TestUrl(id.ToString()));
            Assert.Equal(ImageDownloadStatus.Failed, result.Status);
        }

        Assert.True((await coordinator.GetOrDownloadAsync(ImageCacheKey.Cover(21), TestUrl("current"))).IsSuccess);
        var failures = (IReadOnlyDictionary<ImageCacheKey, DateTimeOffset>)typeof(ImageDownloadCoordinator)
            .GetField("_failures", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator)!;
        Assert.Empty(failures);
        Assert.Equal(21, handler.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownWaitsForActualDownloadAfterSubscribersLeave(bool clearCache)
    {
        using var temp = new TempDirectory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelegateHandler(async (_, _, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            started.TrySetResult();
            // 模拟已经收到取消但仍在收尾的 I/O。
            await release.Task;
            return ImageResponse();
        });
        var coordinator = CreateCoordinator(temp.Path, handler);
        using var cancellation = new CancellationTokenSource();
        var download = coordinator.GetOrDownloadAsync(ImageCacheKey.Cover(1), TestUrl("1"), cancellation.Token);
        Task rejected = Task.CompletedTask;
        try
        {
            await started.Task.WaitAsync(Budget);
            cancellation.Cancel();
            Assert.Equal(ImageDownloadStatus.Cancelled, (await download.WaitAsync(Budget)).Status);
            if (clearCache)
                coordinator.ClearAll();

            var shutdown = coordinator.DisposeAsync().AsTask();
            await cancelled.Task.WaitAsync(Budget);
            Assert.False(shutdown.IsCompleted);
            Assert.Same(shutdown, coordinator.DisposeAsync().AsTask());
            var afterShutdown = coordinator.GetOrDownloadAsync(ImageCacheKey.Cover(2), TestUrl("2"));
            rejected = afterShutdown;
            Assert.Equal(ImageDownloadStatus.Cancelled, (await afterShutdown.WaitAsync(Budget)).Status);
            release.TrySetResult();
            await shutdown.WaitAsync(Budget);
            Assert.False(coordinator.HasLocalCache(ImageCacheKey.Cover(1)));
        }
        finally
        {
            release.TrySetResult();
            await coordinator.DisposeAsync().AsTask().WaitAsync(Budget);
            await ObserveAsync(Task.WhenAll(download, rejected)).WaitAsync(Budget);
        }
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task ExpiredQueueItemDoesNotRemoveANewerCooldownForTheSameImage()
    {
        using var temp = new TempDirectory();
        using var handler = new DelegateHandler((_, request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath == "/current.jpg"
                ? ImageResponse()
                : new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var http = new HttpClient(handler);
        await using var coordinator = new ImageDownloadCoordinator(
            temp.Path, http, retryDelay: TimeSpan.Zero, failureCooldown: TimeSpan.Zero);
        var key = ImageCacheKey.Cover(1);
        Assert.Equal(ImageDownloadStatus.Failed, (await coordinator.GetOrDownloadAsync(key, TestUrl("old"))).Status);

        // 固定“旧到期项尚在队列，但同一作品已写入新失败”的交错，不靠真实计时等待。
        var failures = (IDictionary<ImageCacheKey, DateTimeOffset>)typeof(ImageDownloadCoordinator)
            .GetField("_failures", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator)!;
        var renewed = DateTimeOffset.UtcNow.AddMinutes(1);
        failures[key] = renewed;
        Assert.True((await coordinator.GetOrDownloadAsync(ImageCacheKey.Cover(2), TestUrl("current"))).IsSuccess);

        Assert.Equal(renewed, failures[key]);
        Assert.Equal(ImageDownloadStatus.Failed, (await coordinator.GetOrDownloadAsync(key, TestUrl("old"))).Status);
        Assert.Equal(2, handler.CallCount);
    }

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
        try
        {
            release.SetResult();
            var results = await Task.WhenAll(first, second).WaitAsync(Budget);
            Assert.All(results, result => Assert.True(result.IsSuccess));
            Assert.Equal(1, handler.CallCount);
        }
        finally
        {
            release.TrySetResult();
            await coordinator.DisposeAsync().AsTask().WaitAsync(Budget);
            await ObserveAsync(Task.WhenAll(first, second)).WaitAsync(Budget);
        }
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
        try
        {
            await requestStarted.Task.WaitAsync(Budget);
            cancellation.Cancel();
            var cancelledResult = await cancelled.WaitAsync(Budget);
            release.SetResult();
            var retainedResult = await retained.WaitAsync(Budget);

            Assert.Equal(ImageDownloadStatus.Cancelled, cancelledResult.Status);
            Assert.True(retainedResult.IsSuccess);
            Assert.Equal(1, handler.CallCount);
        }
        finally
        {
            release.TrySetResult();
            await coordinator.DisposeAsync().AsTask().WaitAsync(Budget);
            await ObserveAsync(Task.WhenAll(cancelled, retained)).WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task LastSubscriberCancellation_ReleasesDownloadSlot()
    {
        using var temp = new TempDirectory();
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelegateHandler(async (call, _, token) =>
        {
            if (call == 1)
            {
                firstStarted.SetResult();
                await release.Task.WaitAsync(token);
            }
            secondStarted.TrySetResult();
            return ImageResponse();
        });
        var coordinator = CreateCoordinator(temp.Path, handler, maxConcurrency: 1);
        using var cancellation = new CancellationTokenSource();

        var obsolete = coordinator.GetOrDownloadAsync(
            ImageCacheKey.Cover(1),
            TestUrl("1"),
            cancellation.Token);
        Task current = Task.CompletedTask;
        try
        {
            await firstStarted.Task.WaitAsync(Budget);
            var next = coordinator.GetOrDownloadAsync(ImageCacheKey.Cover(2), TestUrl("2"));
            current = next;
            cancellation.Cancel();

            await secondStarted.Task.WaitAsync(Budget);
            Assert.Equal(ImageDownloadStatus.Cancelled, (await obsolete.WaitAsync(Budget)).Status);
            Assert.True((await next.WaitAsync(Budget)).IsSuccess);
        }
        finally
        {
            release.TrySetResult();
            await coordinator.DisposeAsync().AsTask().WaitAsync(Budget);
            await ObserveAsync(Task.WhenAll(obsolete, current)).WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task TransientFailure_RetriesOnce()
    {
        using var temp = new TempDirectory();
        var handler = new DelegateHandler((call, _, _) => Task.FromResult(
            call == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : ImageResponse()));
        await using var coordinator = CreateCoordinator(temp.Path, handler);

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
        await using var coordinator = CreateCoordinator(temp.Path, handler);
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
        await using var coordinator = CreateCoordinator(temp.Path, handler);

        var result = await coordinator.GetOrDownloadAsync(
            ImageCacheKey.Cover(40),
            TestUrl("40"));

        Assert.Equal(ImageDownloadStatus.Failed, result.Status);
        Assert.Equal(1, handler.CallCount);
        Assert.False(coordinator.HasLocalCache(ImageCacheKey.Cover(40)));
    }

    [Fact]
    public async Task SvgPlaceholder_IsTransientAndIsNotCachedAsCover()
    {
        using var temp = new TempDirectory();
        var handler = new DelegateHandler((_, _, _) =>
        {
            var content = new StringContent("<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
            content.Headers.ContentType = new MediaTypeHeaderValue("image/svg+xml");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            });
        });
        await using var coordinator = CreateCoordinator(temp.Path, handler);
        var key = ImageCacheKey.Cover(44);

        var result = await coordinator.GetOrDownloadAsync(key, TestUrl("44"));

        Assert.Equal(ImageDownloadStatus.Failed, result.Status);
        Assert.Equal(2, handler.CallCount);
        Assert.False(coordinator.HasLocalCache(key));
    }

    [Fact]
    public async Task Invalidate_ClearsFailureCooldownForManualRetry()
    {
        using var temp = new TempDirectory();
        var handler = new DelegateHandler((call, _, _) => Task.FromResult(
            call == 1
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : ImageResponse()));
        await using var coordinator = CreateCoordinator(temp.Path, handler);
        var key = ImageCacheKey.Cover(45);

        var failed = await coordinator.GetOrDownloadAsync(key, TestUrl("45"));
        coordinator.Invalidate(key);
        var retried = await coordinator.GetOrDownloadAsync(key, TestUrl("45"));

        Assert.Equal(ImageDownloadStatus.Failed, failed.Status);
        Assert.True(retried.IsSuccess);
        Assert.Equal(2, handler.CallCount);
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
        await using var coordinator = CreateCoordinator(temp.Path, handler);
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
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelegateHandler(async (_, _, token) =>
        {
            started.SetResult();
            await release.Task.WaitAsync(token);
            return ImageResponse();
        });
        var coordinator = CreateCoordinator(temp.Path, handler);
        var key = ImageCacheKey.Cover(41);

        var download = coordinator.GetOrDownloadAsync(key, TestUrl("41"));
        try
        {
            await started.Task.WaitAsync(Budget);
            coordinator.ClearAll();
            var result = await download.WaitAsync(Budget);

            Assert.Equal(ImageDownloadStatus.Cancelled, result.Status);
            Assert.False(coordinator.HasLocalCache(key));
        }
        finally
        {
            release.TrySetResult();
            await coordinator.DisposeAsync().AsTask().WaitAsync(Budget);
            await ObserveAsync(download).WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task Invalidate_RemovesCorruptCacheAndDownloadsAgain()
    {
        using var temp = new TempDirectory();
        var handler = new DelegateHandler((_, _, _) => Task.FromResult(ImageResponse()));
        await using var coordinator = CreateCoordinator(temp.Path, handler);
        var key = ImageCacheKey.Cover(5);
        await File.WriteAllTextAsync(coordinator.GetLocalPath(key), "broken");

        coordinator.Invalidate(key);
        var result = await coordinator.GetOrDownloadAsync(key, TestUrl("5"));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, handler.CallCount);
        Assert.True(new FileInfo(coordinator.GetLocalPath(key)).Length > "broken".Length);
    }

    [Fact]
    public async Task AvatarCacheKey_IsStableAndSeparateFromCoverCache()
    {
        using var temp = new TempDirectory();
        await using var coordinator = CreateCoordinator(temp.Path, new DelegateHandler(
            (_, _, _) => Task.FromResult(ImageResponse())));

        var first = coordinator.GetLocalPath(ImageCacheKey.Avatar(TestUrl("avatar")));
        var second = coordinator.GetLocalPath(ImageCacheKey.Avatar(TestUrl("avatar")));
        var cover = coordinator.GetLocalPath(ImageCacheKey.Cover(1));

        Assert.Equal(first, second);
        Assert.NotEqual(first, cover);
        Assert.Contains($"{Path.DirectorySeparatorChar}avatars{Path.DirectorySeparatorChar}", first);
    }

    [Fact]
    public async Task ClearAllDuringFailedDownload_DoesNotRestoreTheOldFailureCooldown()
    {
        using var temp = new TempDirectory();
        ImageDownloadCoordinator? owner = null;
        var handler = new DelegateHandler((call, _, _) =>
        {
            if (call == 1)
            {
                owner!.ClearAll();
                throw new InvalidOperationException("Synthetic download failure after cache clearing");
            }
            return Task.FromResult(ImageResponse());
        });
        using var http = new HttpClient(handler);
        await using var coordinator = new ImageDownloadCoordinator(
            temp.Path, http, retryDelay: TimeSpan.Zero);
        owner = coordinator;
        var key = ImageCacheKey.Cover(47);

        var old = await coordinator.GetOrDownloadAsync(key, TestUrl("47"))
            .WaitAsync(TimeSpan.FromSeconds(5));
        var retried = await coordinator.GetOrDownloadAsync(key, TestUrl("47"))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ImageDownloadStatus.Failed, old.Status);
        Assert.True(retried.IsSuccess);
        Assert.Equal(2, handler.CallCount);
        Assert.True(coordinator.HasLocalCache(key));
    }

    [Fact]
    public async Task UnexpectedDownloadFailure_ReturnsFailedAndAllowsShutdown()
    {
        using var temp = new TempDirectory();
        var handler = new DelegateHandler((_, _, _) =>
            throw new InvalidOperationException("Synthetic handler failure"));
        await using var coordinator = CreateCoordinator(temp.Path, handler);

        var result = await coordinator.GetOrDownloadAsync(ImageCacheKey.Cover(46), TestUrl("46"));

        Assert.Equal(ImageDownloadStatus.Failed, result.Status);
        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task ObserveAsync(Task task)
        => await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

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
