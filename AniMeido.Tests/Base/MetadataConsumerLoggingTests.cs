using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using AniMeido.Plugin.Base.Views;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;

namespace AniMeido.Tests;

public sealed partial class MetadataConsumerLoggingTests : DbTestBase
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private static readonly RecommendationFeature KnownTag =
        new(RecommendationFeatureKind.Tag, "科幻", "科幻");

    [Theory]
    [InlineData("http")]
    [InlineData("metadata")]
    [InlineData("timeout")]
    public async Task Logging_LocalSearchWarnsForSkippedDetailsAndKeepsResultOrder(string failureKind)
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        await tracking.SetStatusAsync(1, AnimeTrackingStatus.Watching);
        await tracking.SetStatusAsync(2, AnimeTrackingStatus.Following);
        await tracking.SetStatusAsync(3, AnimeTrackingStatus.Completed);
        var failure = Failure(failureKind);
        var source = new ConsumerSource
        {
            Detail = (id, _) => id == 2
                ? Task.FromException<Anime?>(failure)
                : Task.FromResult<Anime?>(Anime(id)),
        };
        var logger = new ConsumerLogger<LocalSearchService>();
        var service = new LocalSearchService(tracking, source, logger);

        var results = await service.SearchTrackedAsync("match").WaitAsync(Budget);

        Assert.Equal(new[] { 1, 3 }, results.Select(item => item.Anime.ID));
        Assert.Equal(new[] { AnimeTrackingStatus.Watching, AnimeTrackingStatus.Completed },
            results.Select(item => item.TrackingStatus));
        AssertLog(logger, LogLevel.Warning, failure);
    }

    [Fact]
    public async Task Logging_LocalSearchDoesNotRecordActiveCancellation()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        await tracking.SetStatusAsync(1, AnimeTrackingStatus.Watching);
        var started = NewSignal();
        var release = NewSignal();
        var source = new ConsumerSource
        {
            Detail = async (id, token) =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
                return Anime(id);
            },
        };
        var logger = new ConsumerLogger<LocalSearchService>();
        var service = new LocalSearchService(tracking, source, logger);
        using var cancellation = new CancellationTokenSource();
        var searching = service.SearchTrackedAsync("match", cancellation.Token);
        try
        {
            await started.Task.WaitAsync(Budget);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => searching.WaitAsync(Budget));
            Assert.Empty(logger.Entries);
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            await ObserveAsync(searching).WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task Logging_LocalSearchStillPropagatesUnrequestedPlainCancellation()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        await tracking.SetStatusAsync(1, AnimeTrackingStatus.Watching);
        var source = new ConsumerSource
        {
            Detail = (_, _) => Task.FromException<Anime?>(new OperationCanceledException("synthetic cancellation")),
        };
        var logger = new ConsumerLogger<LocalSearchService>();
        var service = new LocalSearchService(tracking, source, logger);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SearchTrackedAsync("match").WaitAsync(Budget));

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task Logging_CandidateDetailFallbackWarnsAndPreservesTheOriginalCandidate()
    {
        var failure = new HttpRequestException("synthetic detail failure");
        var original = Anime(1);
        var source = new ConsumerSource
        {
            Detail = (_, _) => Task.FromException<Anime?>(failure),
            TagSearch = _ => Task.FromResult<(List<Anime>, int)>(([original], 1)),
        };
        var logger = new ConsumerLogger<RecommendationCandidateProvider>();
        await using var provider = new RecommendationCandidateProvider(source, logger);
        var round = provider.CreateRound([Profile()], new HashSet<int>());

        var candidates = await provider.GetNextWindowAsync(round, 30, CancellationToken.None).WaitAsync(Budget);

        var candidate = Assert.Single(candidates);
        Assert.Same(original, candidate.Anime);
        Assert.Contains(candidate.Features, feature => feature == KnownTag);
        AssertLog(logger, LogLevel.Warning, failure);
    }

    [Fact]
    public async Task Logging_CandidateFeatureFallbackWarnsAndPreservesOtherFeatureCategories()
    {
        var failure = new MetadataServiceException("synthetic tag failure");
        var source = new ConsumerSource
        {
            Tags = (_, _) => Task.FromException<List<Tag>>(failure),
        };
        var logger = new ConsumerLogger<RecommendationCandidateProvider>();
        await using var provider = new RecommendationCandidateProvider(source, logger);

        var features = await provider.GetFeaturesAsync(
            [new RecommendationSeed(1, "match 1", 1)], CancellationToken.None).WaitAsync(Budget);

        var feature = Assert.Single(features[1]);
        Assert.Equal(RecommendationFeatureKind.Studio, feature.Kind);
        Assert.Equal("100", feature.Key);
        AssertLog(logger, LogLevel.Warning, failure);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Logging_CandidateEnrichmentStillPropagatesCancellationWithoutFallback(
        bool detail, bool taskCancellation)
    {
        OperationCanceledException failure = taskCancellation
            ? new TaskCanceledException("synthetic timeout")
            : new OperationCanceledException("synthetic cancellation");
        var source = new ConsumerSource
        {
            Detail = (_, _) => Task.FromException<Anime?>(failure),
            Tags = (_, _) => Task.FromException<List<Tag>>(failure),
        };
        var logger = new ConsumerLogger<RecommendationCandidateProvider>();
        await using var provider = new RecommendationCandidateProvider(source, logger);
        Task operation = detail
            ? provider.ResolveTitlesAsync([new RecommendationSeed(1, string.Empty, 1)], CancellationToken.None)
            : provider.GetFeaturesAsync([new RecommendationSeed(1, "match 1", 1)], CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(Budget));

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task Logging_RecommendationLoadFailureIsRecordedAtError()
    {
        await RunProductionMigrationAsync();
        await using (var connection = await DbFactory.OpenAsync())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE recommendation_hidden_anime";
            await command.ExecuteNonQueryAsync();
        }
        await using var candidates = new RecommendationCandidateProvider(
            new ConsumerSource(), NullLogger<RecommendationCandidateProvider>.Instance);
        await using var service = CreateService(candidates);
        var logger = new ConsumerLogger<RecommendationPage>();
        var vm = new RecommendationViewModel(service, Browse(), logger);

        await vm.LoadAsync().WaitAsync(Budget);

        Assert.True(vm.HasError);
        Assert.Equal(2, vm.Items.Count);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.IsType<SqliteException>(entry.Exception);
    }

    [Theory]
    [InlineData("refresh", "http")]
    [InlineData("refresh", "timeout")]
    [InlineData("more", "http")]
    [InlineData("more", "timeout")]
    public async Task Logging_RecommendationFailedOperationIsErrorEvenWhenPreviousResultsRemain(
        string operation, string failureKind)
    {
        await RunProductionMigrationAsync();
        var failure = Failure(failureKind);
        var source = new ConsumerSource
        {
            Popular = _ => Task.FromException<List<Anime>>(failure),
            TagSearch = _ => Task.FromException<(List<Anime>, int)>(failure),
        };
        await using var candidates = new RecommendationCandidateProvider(
            source, NullLogger<RecommendationCandidateProvider>.Instance);
        await using var service = CreateService(candidates);
        var logger = new ConsumerLogger<RecommendationPage>();
        var vm = new RecommendationViewModel(service, Browse(personalized: operation == "more"), logger);
        await vm.LoadAsync().WaitAsync(Budget);
        Assert.False(vm.HasError);
        Assert.Empty(logger.Entries);
        var items = vm.Items;

        await RunRecommendationOperationAsync(vm, operation).WaitAsync(Budget);

        Assert.Same(items, vm.Items);
        Assert.Equal(new[] { 1, 2 }, vm.Items.Select(item => item.Anime.ID));
        if (operation == "refresh")
        {
            Assert.True(vm.HasError);
            Assert.True(source.PopularCalls > 0);
        }
        else
        {
            Assert.NotNull(vm.LoadMoreError);
            Assert.True(source.TagSearchCalls > 0);
        }
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.NotNull(entry.Exception);
        if (failureKind == "timeout" || operation == "refresh")
            Assert.Same(failure, entry.Exception);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("timeout")]
    public async Task Logging_RecommendationTagPreviewFallbackWarnsAndPreservesKnownTags(string failureKind)
    {
        await RunProductionMigrationAsync();
        var failure = Failure(failureKind);
        var source = new ConsumerSource
        {
            Tags = (_, _) => Task.FromException<List<Tag>>(failure),
        };
        await using var candidates = new RecommendationCandidateProvider(
            source, NullLogger<RecommendationCandidateProvider>.Instance);
        await using var service = CreateService(candidates);
        var logger = new ConsumerLogger<RecommendationPage>();
        var vm = new RecommendationViewModel(service, Browse(), logger);
        await vm.LoadAsync().WaitAsync(Budget);
        Assert.False(vm.HasError);
        Assert.Empty(logger.Entries);
        var knownKeys = vm.SelectedTags.Select(tag => tag.Feature.Key).ToArray();
        Assert.NotEmpty(knownKeys);

        await vm.LoadSelectedTagsAsync().WaitAsync(Budget);

        Assert.NotNull(vm.TagError);
        Assert.Equal(knownKeys, vm.SelectedTags.Select(tag => tag.Feature.Key));
        AssertLog(logger, LogLevel.Warning, failure);
    }

    [Theory]
    [InlineData("refresh", false)]
    [InlineData("refresh", true)]
    [InlineData("more", false)]
    [InlineData("more", true)]
    [InlineData("tags", false)]
    [InlineData("tags", true)]
    public async Task Logging_RecommendationDoesNotRecordStaleOrActivelyCancelledOperations(
        string operation, bool cancel)
    {
        await RunProductionMigrationAsync();
        var started = NewSignal();
        var release = NewSignal();
        var failure = new HttpRequestException("synthetic old failure");
        async Task<T> PendingAsync<T>()
        {
            started.TrySetResult();
            await release.Task;
            throw new InvalidOperationException("The test must release the request with a failure.");
        }
        var source = new ConsumerSource
        {
            Popular = _ => PendingAsync<List<Anime>>(),
            TagSearch = _ => PendingAsync<(List<Anime>, int)>(),
            Tags = (_, _) => PendingAsync<List<Tag>>(),
        };
        await using var candidates = new RecommendationCandidateProvider(
            source, NullLogger<RecommendationCandidateProvider>.Instance);
        await using var service = CreateService(candidates);
        var logger = new ConsumerLogger<RecommendationPage>();
        var vm = new RecommendationViewModel(service, Browse(personalized: operation == "more"), logger);
        await vm.LoadAsync().WaitAsync(Budget);
        Assert.False(vm.HasError);
        Assert.Empty(logger.Entries);
        using var cancellation = new CancellationTokenSource();
        var pending = RunRecommendationOperationAsync(vm, operation, cancellation.Token);
        try
        {
            await started.Task.WaitAsync(Budget);
            if (cancel)
            {
                cancellation.Cancel();
                release.TrySetCanceled(cancellation.Token);
            }
            else
            {
                vm.Suspend();
                release.TrySetException(failure);
            }
            await pending.WaitAsync(Budget);

            Assert.Equal(new[] { 1, 2 }, vm.Items.Select(item => item.Anime.ID));
            Assert.Empty(logger.Entries);
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetException(failure);
            await ObserveAsync(pending).WaitAsync(Budget);
        }
    }

    private RecommendationService CreateService(RecommendationCandidateProvider candidates)
        => new(DbFactory, new TrackingService(DbFactory), new SavedTagService(DbFactory),
            new ArchiveService(DbFactory), new BrowseHistoryService(DbFactory),
            new ActionCenterService(DbFactory), new CacheService(DbFactory), candidates);

    private static RecommendationBrowseState Browse(bool personalized = false)
        => new()
        {
            Snapshot = new RecommendationSnapshot(RecommendationSnapshot.CurrentSchemaVersion,
                DateTimeOffset.UtcNow, personalized, [Recommendation(1), Recommendation(2)],
                personalized ? [Profile()] : null, HasMore: personalized),
            Profile = [Profile()],
        };

    private static RecommendationItem Recommendation(int id)
        => new(Anime(id), 1, [new RecommendationReason(KnownTag, 1, false, "合成依据")], true, true);

    private static RecommendationFeatureProfile Profile()
        => new(KnownTag, 1, null, []);

    private static Anime Anime(int id)
        => new(id, $"match {id}", null, [], null, null, string.Empty, 2026, 1);

    private static Exception Failure(string kind)
        => kind switch
        {
            "http" => new HttpRequestException("synthetic HTTP failure"),
            "metadata" => new MetadataServiceException("synthetic metadata failure"),
            "timeout" => new TaskCanceledException("synthetic timeout"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static Task RunRecommendationOperationAsync(
        RecommendationViewModel vm, string operation, CancellationToken cancellationToken = default)
        => operation switch
        {
            "refresh" => vm.RefreshAsync(cancellationToken),
            "more" => vm.LoadMoreAsync(cancellationToken),
            "tags" => vm.LoadSelectedTagsAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private static void AssertLog<T>(ConsumerLogger<T> logger, LogLevel level, Exception failure)
    {
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(level, entry.Level);
        Assert.Same(failure, entry.Exception);
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task ObserveAsync(Task operation)
        => await operation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    private sealed record LogEntry(LogLevel Level, Exception? Exception);

    private sealed class ConsumerLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                Entries.Enqueue(new LogEntry(logLevel, exception));
        }
    }

    private sealed class ConsumerSource : IAnimeDataSource
    {
        private int _popularCalls;
        private int _tagSearchCalls;
        internal int PopularCalls => Volatile.Read(ref _popularCalls);
        internal int TagSearchCalls => Volatile.Read(ref _tagSearchCalls);
        internal Func<int, CancellationToken, Task<Anime?>> Detail { get; init; }
            = (id, _) => Task.FromResult<Anime?>(Anime(id));
        internal Func<int, CancellationToken, Task<List<Tag>>> Tags { get; init; }
            = (_, _) => Task.FromResult<List<Tag>>([new Tag("科幻")]);
        internal Func<CancellationToken, Task<List<Anime>>> Popular { get; init; }
            = _ => throw new NotSupportedException("Unexpected popular request.");
        internal Func<CancellationToken, Task<(List<Anime> Results, int Total)>> TagSearch { get; init; }
            = _ => throw new NotSupportedException("Unexpected tag-search request.");

        public Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct) => Detail(animeID, ct);
        public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct) => Tags(animeID, ct);
        public Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct)
            => Task.FromResult<List<Studio>>([new Studio(100, "合成制作方", null)]);
        public Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct)
            => Task.FromResult(new List<VoiceActor>());

        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct)
        {
            Interlocked.Increment(ref _popularCalls);
            return Popular(ct);
        }

        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(
            string tag, int offset, string sort, CancellationToken ct,
            string? airDateFrom = null, string? airDateTo = null)
        {
            Interlocked.Increment(ref _tagSearchCalls);
            return TagSearch(ct);
        }

        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct) => throw new NotSupportedException();
        public Task<(List<Anime> Results, int Total, int NextOffset)> SearchByKeywordAsync(
            string keyword, int offset, CancellationToken ct) => throw new NotSupportedException();
    }
}
