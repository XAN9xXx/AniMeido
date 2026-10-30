using System.Net;
using System.Net.Http.Headers;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class PublicServiceRequestsTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Headerless429WaitsTwentySecondsOutsideTheNetworkAttemptAndRetriesOnce()
    {
        var time = new ServiceTestTime();
        using var handler = new Handler(call => call == 1 ? Limited(20) : Ok());
        using var http = Client(handler);
        var requests = new PublicServiceRequests(http, time: time);
        var reading = Read(requests);
        try
        {
            await time.WaitForDelayAsync(TimeSpan.FromSeconds(20)).WaitAsync(Budget);
            Assert.False(reading.IsCompleted);
            Assert.Equal(1, handler.Calls);
            time.Advance(TimeSpan.FromSeconds(20));
            Assert.Equal("ok", await reading.WaitAsync(Budget));
            Assert.Equal(2, handler.Calls);
        }
        finally { time.Advance(TimeSpan.FromDays(1)); await Observe(reading); }
    }

    [Fact]
    public async Task AReadBudgetContinuesAcrossPagesAndKeepsTheLongerServiceCooldown()
    {
        var time = new ServiceTestTime();
        using var handler = new Handler(call => call switch { 1 => Limited(20), 3 => Limited(15), _ => Ok() });
        using var http = Client(handler);
        var requests = new PublicServiceRequests(http, time: time);
        var budget = new ServiceReadBudget();
        var first = Read(requests, budget);
        try
        {
            await time.WaitForDelayAsync(TimeSpan.FromSeconds(20)).WaitAsync(Budget);
            time.Advance(TimeSpan.FromSeconds(20));
            await first.WaitAsync(Budget);
            Assert.Equal(TimeSpan.FromSeconds(10), budget.Remaining);
            await Assert.ThrowsAsync<MetadataServiceException>(() => Read(requests, budget).WaitAsync(Budget));
            Assert.Equal(3, handler.Calls);
            time.Advance(TimeSpan.FromSeconds(15));
            Assert.Equal("ok", await Read(requests).WaitAsync(Budget));
        }
        finally { time.Advance(TimeSpan.FromDays(1)); await Observe(first); }
    }

    [Fact]
    public async Task Final429StillDelaysTheNextReadAndLongCooldownReturnsWithoutSending()
    {
        var time = new ServiceTestTime();
        using var handler = new Handler(call => call == 1 ? Limited(0) : call == 2 ? Limited(60) : Ok());
        using var http = Client(handler);
        var requests = new PublicServiceRequests(http, time: time);
        await Assert.ThrowsAsync<MetadataServiceException>(() => Read(requests).WaitAsync(Budget));
        Assert.Equal(2, handler.Calls);
        await Assert.ThrowsAsync<MetadataServiceException>(() => Read(requests).WaitAsync(Budget));
        Assert.Equal(2, handler.Calls);
        time.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal("ok", await Read(requests).WaitAsync(Budget));
    }

    [Fact]
    public async Task ShorterConcurrent429CannotShortenTheSharedCooldownAndCancellingOneWaiterKeepsTheOther()
    {
        var time = new ServiceTestTime();
        using var handler = new Handler(call => call <= 2 ? Limited(call == 1 ? 20 : 5) : Ok());
        using var http = Client(handler);
        var requests = new PublicServiceRequests(http, time: time);
        await Assert.ThrowsAsync<MetadataServiceException>(() => requests.ReadOnceAsync("/", Content, default, new()).WaitAsync(Budget));
        // 第二个已发送请求的反馈，按同一服务的规则合并。
        using var shorter = Limited(5);
        requests.ObserveRateLimit(shorter);
        using var cancelled = new CancellationTokenSource();
        var first = Read(requests, ct: cancelled.Token);
        var second = Read(requests);
        try
        {
            await time.WaitForDelayAsync(TimeSpan.FromSeconds(20)).WaitAsync(Budget);
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Budget));
            time.Advance(TimeSpan.FromSeconds(5));
            Assert.Equal(1, handler.Calls);
            Assert.False(second.IsCompleted);
            time.Advance(TimeSpan.FromSeconds(15));
            // 预留的第二次限流反馈为 5 秒，仍需等待后才重发。
            await time.WaitForDelayAsync(TimeSpan.FromSeconds(5)).WaitAsync(Budget);
            time.Advance(TimeSpan.FromSeconds(5));
            Assert.Equal("ok", await second.WaitAsync(Budget));
        }
        finally { cancelled.Cancel(); time.Advance(TimeSpan.FromDays(1)); await Observe(first, second); }
    }

    [Fact]
    public async Task ConcurrencyPermitCoversTheWholeBodyAndTheFifthRequestWaits()
    {
        var release = Signal();
        var entered = new SemaphoreSlim(0);
        using var handler = new Handler(_ => Ok());
        using var http = Client(handler);
        var requests = new PublicServiceRequests(http);
        var reads = Enumerable.Range(0, 5).Select(_ => requests.ReadAsync("/", async (_, ct) =>
        {
            entered.Release();
            await release.Task.WaitAsync(ct);
            return "done";
        }, default)).ToArray();
        try
        {
            for (var i = 0; i < 4; i++) Assert.True(await entered.WaitAsync(Budget));
            Assert.Equal(4, handler.Calls);
            Assert.False(reads[4].IsCompleted);
            release.TrySetResult();
            await Task.WhenAll(reads).WaitAsync(Budget);
            Assert.Equal(5, handler.Calls);
        }
        finally { release.TrySetResult(); await Observe(reads); entered.Dispose(); }
    }

    [Fact]
    public async Task AttemptTimeoutCancelsBodyButRetainsThePermitUntilActualWorkEnds()
    {
        var time = new ServiceTestTime();
        var entered = Signal();
        var release = Signal();
        using var handler = new Handler(_ => Ok());
        using var http = Client(handler);
        var requests = new PublicServiceRequests(http, time: time, maxConcurrency: 1);
        var first = requests.ReadAsync("/", async (_, _) => { entered.TrySetResult(); await release.Task; return "late"; }, default);
        Task<string>? second = null;
        try
        {
            await entered.Task.WaitAsync(Budget);
            time.Advance(TimeSpan.FromSeconds(10));
            second = Read(requests);
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.Equal(1, handler.Calls);
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Budget));
            Assert.Equal("ok", await second.WaitAsync(Budget));
        }
        finally { release.TrySetResult(); time.Advance(TimeSpan.FromDays(1)); await Observe(first, second); }
    }

    [Theory]
    [InlineData("date", 12)]
    [InlineData("missing", 2)]
    [InlineData("invalid", 2)]
    public async Task RetryAfterDatesAndFallbackValuesUseTheExpectedCooldown(string kind, int seconds)
    {
        var time = new ServiceTestTime();
        using var handler = new Handler(call =>
        {
            if (call > 1) return Ok();
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            if (kind == "date") response.Headers.RetryAfter = new RetryConditionHeaderValue(time.GetUtcNow().AddSeconds(seconds));
            if (kind == "invalid") response.Headers.TryAddWithoutValidation("Retry-After", "invalid");
            return response;
        });
        using var http = Client(handler);
        var reading = Read(new PublicServiceRequests(http, time: time));
        try
        {
            await time.WaitForDelayAsync(TimeSpan.FromSeconds(seconds)).WaitAsync(Budget);
            time.Advance(TimeSpan.FromSeconds(seconds));
            Assert.Equal("ok", await reading.WaitAsync(Budget));
            Assert.Equal(2, handler.Calls);
        }
        finally { time.Advance(TimeSpan.FromDays(1)); await Observe(reading); }
    }

    [Fact]
    public async Task ExtendingAnExistingCooldownUsesTheRemainingBudgetInsteadOfStartingAnotherThirtySeconds()
    {
        var time = new ServiceTestTime();
        using var handler = new Handler(_ => Limited(20));
        using var http = Client(handler);
        var requests = new PublicServiceRequests(http, time: time);
        await Assert.ThrowsAsync<MetadataServiceException>(() => requests.ReadOnceAsync("/", Content, default, new()));
        var reading = Read(requests);
        try
        {
            await time.WaitForDelayAsync(TimeSpan.FromSeconds(20)).WaitAsync(Budget);
            time.Advance(TimeSpan.FromSeconds(5));
            using var extension = Limited(60);
            requests.ObserveRateLimit(extension);
            time.Advance(TimeSpan.FromSeconds(15));
            var error = await Assert.ThrowsAsync<MetadataServiceException>(() => reading.WaitAsync(Budget));
            Assert.True(error.IsRateLimited);
            Assert.Equal(1, handler.Calls);
        }
        finally { time.Advance(TimeSpan.FromDays(1)); await Observe(reading); }
    }

    private static Task<string> Read(PublicServiceRequests requests, ServiceReadBudget? budget = null, CancellationToken ct = default)
        => requests.ReadAsync("/", Content, ct, budget);
    private static Task<string> Content(HttpResponseMessage response, CancellationToken ct)
    {
        response.EnsureSuccessStatusCode();
        return response.Content.ReadAsStringAsync(ct);
    }
    private static HttpClient Client(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("https://api.example.test") };
    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK) { Content = new StringContent("ok") };
    private static HttpResponseMessage Limited(int seconds) => new(HttpStatusCode.TooManyRequests)
    {
        Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds)) },
    };
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Observe(params Task?[] tasks)
    {
        foreach (var task in tasks)
            if (task is not null) await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }
    private sealed class Handler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(Interlocked.Increment(ref _calls)));
    }
}
