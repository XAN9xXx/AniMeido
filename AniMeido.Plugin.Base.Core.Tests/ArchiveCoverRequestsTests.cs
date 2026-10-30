using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMeido.Tests;

public sealed partial class ArchiveCoverRequestsTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task SharedRequestAndCompletedResultAreReused()
    {
        var response = new TaskCompletionSource<Anime?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var covers = new ArchiveCoverRequests((_, _) =>
        {
            calls++;
            return response.Task;
        }, NullLogger.Instance);
        var first = covers.GetOrStart(1, default);
        try
        {
            Assert.Same(first, covers.GetOrStart(1, default));
            Assert.True(covers.IsCurrent(1, first));
            Assert.False(covers.IsCurrent(2, first));
            response.SetResult(Item(1));
            Assert.Equal(1, (await first.WaitAsync(Budget))!.ID);
            Assert.Same(first, covers.GetOrStart(1, default));
            Assert.Equal(1, calls);
        }
        finally
        {
            response.TrySetResult(null);
            await ObserveAsync(first).WaitAsync(Budget);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledRequestCanBeRetried(bool cancel)
    {
        var calls = 0;
        var failure = new HttpRequestException("fixture failure");
        var covers = new ArchiveCoverRequests((_, _) => ++calls == 1
            ? cancel ? Task.FromCanceled<Anime?>(new CancellationToken(true))
                : Task.FromException<Anime?>(failure)
            : Task.FromResult<Anime?>(Item(1)), NullLogger.Instance);
        var first = covers.GetOrStart(1, default);
        await ObserveAsync(first).WaitAsync(Budget);
        Assert.False(first.IsCompletedSuccessfully);

        var retry = covers.GetOrStart(1, default);
        Assert.NotSame(first, retry);
        Assert.False(covers.IsCurrent(1, first));
        Assert.True(covers.IsCurrent(1, retry));
        Assert.Equal(1, (await retry.WaitAsync(Budget))!.ID);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task LeavingKeepsCompletedDataButLateOldResultsCannotReplaceNewRequests()
    {
        var response = new TaskCompletionSource<Anime?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCalls = 0;
        var covers = new ArchiveCoverRequests((id, _) => id == 2 && ++secondCalls == 1
            ? response.Task : Task.FromResult<Anime?>(Item(id)), NullLogger.Instance);
        var complete = covers.GetOrStart(1, default);
        await complete.WaitAsync(Budget);
        var old = covers.GetOrStart(2, default);
        try
        {
            covers.DiscardIncomplete();
            Assert.Same(complete, covers.GetOrStart(1, default));
            var current = covers.GetOrStart(2, default);
            Assert.NotSame(old, current);
            response.SetResult(Item(20));
            await old.WaitAsync(Budget);
            Assert.True(covers.IsCurrent(2, current));
            Assert.False(covers.IsCurrent(2, old));
            Assert.Equal(2, (await current.WaitAsync(Budget))!.ID);
        }
        finally
        {
            response.TrySetResult(null);
            await ObserveAsync(old).WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task IdentityChangesDiscardCompletedAndRunningRequests()
    {
        var response = new TaskCompletionSource<Anime?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var covers = new ArchiveCoverRequests((id, _) => id == 2
            ? response.Task : Task.FromResult<Anime?>(Item(id)), NullLogger.Instance);
        var complete = covers.GetOrStart(1, default);
        var pending = covers.GetOrStart(2, default);
        try
        {
            await complete.WaitAsync(Budget);
            covers.Clear();
            Assert.False(covers.IsCurrent(1, complete));
            Assert.False(covers.IsCurrent(2, pending));
            Assert.NotSame(complete, covers.GetOrStart(1, default));
            response.SetResult(Item(2));
            await pending.WaitAsync(Budget);
            Assert.False(covers.IsCurrent(2, pending));
        }
        finally
        {
            response.TrySetResult(null);
            await ObserveAsync(pending).WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task MissingDetailIsACompletedResultNotAnAutomaticRetry()
    {
        var calls = 0;
        var covers = new ArchiveCoverRequests((_, _) =>
        {
            calls++;
            return Task.FromResult<Anime?>(null);
        }, NullLogger.Instance);
        var request = covers.GetOrStart(-1, default);
        Assert.Null(await request.WaitAsync(Budget));
        covers.DiscardIncomplete();
        Assert.Same(request, covers.GetOrStart(-1, default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AtMostFourFetchesRunAndCancelledQueueDoesNotEnterTheSource()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var covers = new ArchiveCoverRequests(async (id, _) =>
        {
            Interlocked.Increment(ref calls);
            await release.Task;
            return Item(id);
        }, NullLogger.Instance);
        using var cancellation = new CancellationTokenSource();
        var active = Enumerable.Range(1, 4).Select(id => covers.GetOrStart(id, default)).ToArray();
        var queued = covers.GetOrStart(5, cancellation.Token);
        var next = covers.GetOrStart(6, default);
        try
        {
            Assert.Equal(4, Volatile.Read(ref calls));
            Assert.False(next.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Budget));
            Assert.Equal(4, Volatile.Read(ref calls));
            release.SetResult();
            await Task.WhenAll(active).WaitAsync(Budget);
            Assert.Equal(6, (await next.WaitAsync(Budget))!.ID);
            Assert.Equal(5, Volatile.Read(ref calls));
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            await ObserveAsync(Task.WhenAll(active.Append(queued).Append(next))).WaitAsync(Budget);
        }
    }

    private static Anime Item(int id) => new(id, $"fixture {id}", null, [], null, null, "", 2026, 1);

    private static async Task ObserveAsync(Task task)
        => await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
}
