using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.Logging;

namespace AniMeido.Tests;

public sealed partial class MetadataConsumerLoggingTests
{
    [Theory]
    [InlineData("http")]
    [InlineData("io")]
    public async Task Logging_CancelledCandidateSourceDoesNotWarnForALateFailure(string failureKind)
    {
        var started = NewSignal();
        var companionFinished = NewSignal();
        var cancellationReachedSource = NewSignal();
        var release = NewSignal();
        var failure = CandidateSourceFailure(failureKind);
        var calls = 0;
        var source = new ConsumerSource
        {
            TagSearch = async token =>
            {
                if (Interlocked.Increment(ref calls) != 1)
                {
                    companionFinished.TrySetResult();
                    return ([], 0);
                }

                using var registration = token.Register(() => cancellationReachedSource.TrySetResult());
                started.TrySetResult();
                await release.Task;
                throw failure;
            },
        };
        var logger = new ConsumerLogger<RecommendationCandidateProvider>();
        await using var provider = new RecommendationCandidateProvider(source, logger);
        var round = provider.CreateRound([Profile()], new HashSet<int>());
        using var cancellation = new CancellationTokenSource();
        var pending = provider.GetNextWindowAsync(round, 1, cancellation.Token);
        try
        {
            await Task.WhenAll(started.Task, companionFinished.Task).WaitAsync(Budget);
            cancellation.Cancel();
            await cancellationReachedSource.Task.WaitAsync(Budget);
            release.TrySetResult();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Budget));

            Assert.Equal(2, Volatile.Read(ref calls));
            Assert.Empty(logger.Entries);
            Assert.True(round.HasMore);
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            await ObserveAsync(pending).WaitAsync(Budget);
        }
    }

    [Theory]
    [InlineData("http")]
    [InlineData("io")]
    public async Task Logging_CandidateSourceFailureWarnsOnceAndCanRetryOnTheNextWindow(string failureKind)
    {
        var failure = CandidateSourceFailure(failureKind);
        var calls = 0;
        var source = new ConsumerSource
        {
            TagSearch = _ => Interlocked.Increment(ref calls) switch
            {
                1 => Task.FromException<(List<Anime>, int)>(failure),
                2 => Task.FromResult<(List<Anime>, int)>(([Anime(301)], 1)),
                3 => Task.FromResult<(List<Anime>, int)>(([Anime(302)], 1)),
                _ => throw new InvalidOperationException("Unexpected candidate query."),
            },
        };
        var logger = new ConsumerLogger<RecommendationCandidateProvider>();
        await using var provider = new RecommendationCandidateProvider(source, logger);
        var round = provider.CreateRound([Profile()], new HashSet<int>());

        var first = await provider.GetNextWindowAsync(round, 1, CancellationToken.None).WaitAsync(Budget);

        Assert.Equal(301, Assert.Single(first).Anime.ID);
        Assert.Equal(2, Volatile.Read(ref calls));
        Assert.True(round.HasMore);
        AssertLog(logger, LogLevel.Warning, failure);
        round.Consume(first.Select(item => item.Anime.ID));

        var next = await provider.GetNextWindowAsync(round, 1, CancellationToken.None).WaitAsync(Budget);

        Assert.Equal(302, Assert.Single(next).Anime.ID);
        Assert.Equal(3, Volatile.Read(ref calls));
        AssertLog(logger, LogLevel.Warning, failure);
        round.Consume(next.Select(item => item.Anime.ID));
        Assert.False(round.HasMore);
    }

    private static Exception CandidateSourceFailure(string kind)
        => kind switch
        {
            "http" => new HttpRequestException("synthetic candidate HTTP failure"),
            "io" => new IOException("synthetic candidate IO failure"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
}
