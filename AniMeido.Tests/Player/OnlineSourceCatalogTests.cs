using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Player.Sources;
using AniMeido.Plugin.Player.Services;
using AniMeido.Plugin.Player.Playback;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace AniMeido.Tests;

public sealed partial class OnlineSourceCatalogTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GetEpisodes_SkipsFailedSourceAndReportsOneWarning(
        bool lazySources, bool sourceCancellation)
    {
        var logger = new CatalogLogger();
        Exception failure = sourceCancellation
            ? new OperationCanceledException("Synthetic source timeout")
            : new InvalidOperationException("Synthetic source failure");
        IOnlineAnimeSource[] sources =
        [
            new ErrorSource(failure), new WorkingSource(), new ReplacementSource(),
        ];
        var catalog = lazySources
            ? new OnlineSourceCatalog(() => sources, logger: logger)
            : new OnlineSourceCatalog(sources, logger: logger);

        var episodes = await catalog.GetEpisodesAsync(
            new AnimePlaybackContext(42, "Test Anime"), CancellationToken.None).WaitAsync(Budget);

        Assert.Equal(new[] { "replacement", "working" },
            episodes.Select(item => item.Episode.SourceId).Order(StringComparer.Ordinal));
        Assert.All(episodes, item => Assert.Equal("episode-1", item.Episode.EpisodeId));
        var diagnostic = Assert.Single(catalog.LastDiagnostics);
        Assert.Equal("failing", diagnostic.SourceId);
        Assert.Equal("查找剧集", diagnostic.Operation);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Same(failure, entry.Exception);
        Assert.Equal("failing", entry.Properties["SourceId"]);
    }

    [Fact]
    public async Task GetEpisodes_CallerCancellationPropagatesWithoutLogging()
    {
        var logger = new CatalogLogger();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new OnlineSourceCatalog([new BlockingSource(started, release)], logger: logger);
        var loading = catalog.GetEpisodesAsync(
            new AnimePlaybackContext(42, "Test Anime"), cancellation.Token);
        try
        {
            await started.Task.WaitAsync(Budget);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading.WaitAsync(Budget));
            Assert.Empty(logger.Entries);
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading.WaitAsync(Budget));
        }
    }

    [Fact]
    public async Task Resolve_PropagatesFailureWithoutIntermediateLogging()
    {
        var logger = new CatalogLogger();
        var failure = new InvalidOperationException("Synthetic resolve failure");
        var catalog = new OnlineSourceCatalog([new ErrorSource(failure)], logger: logger);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => catalog.ResolveAsync(new SourceEpisode("failing", "episode-1", "Episode 1"), default));

        Assert.Same(failure, error.InnerException);
        Assert.Equal("解析媒体", Assert.Single(catalog.LastDiagnostics).Operation);
        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposingWhileSourcesLoadPreventsOpeningANativeWindow(bool settings)
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new OnlineSourceCatalog(() =>
        {
            started.TrySetResult();
            if (!release.Wait(Budget)) throw new TimeoutException("fixture source loader timed out");
            return [new WorkingSource()];
        });
        var manager = new PlayerWindowManager(catalog, null!, null!, null!, null!, null!, null!,
            new PlayerExperienceSettingsStore(path: null), null!);
        var opening = settings
            ? manager.OpenSettingsAsync(PlayerWindowManager.PlayerSettingsId)
            : manager.LaunchAsync(new AnimePlaybackContext(42, "Test Anime"));
        try
        {
            await started.Task.WaitAsync(Budget);
            await manager.DisposeAsync();
        }
        finally
        {
            release.Set();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => opening.WaitAsync(Budget));
            await manager.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConstructionDoesNotLoadSourcesAndInitializationRunsOffTheUiContext()
    {
        var ui = new PausedUiContext();
        var loads = 0;
        SynchronizationContext? loaderContext = ui;
        try
        {
            var previous = SynchronizationContext.Current;
            OnlineSourceCatalog catalog;
            Task initialization;
            try
            {
                SynchronizationContext.SetSynchronizationContext(ui);
                catalog = new OnlineSourceCatalog(() =>
                {
                    Interlocked.Increment(ref loads);
                    loaderContext = SynchronizationContext.Current;
                    return [new WorkingSource()];
                });
                Assert.Equal(0, loads);
                Assert.Equal(0, catalog.SourceCount);
                initialization = catalog.EnsureLoadedAsync(CancellationToken.None);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }

            await initialization.WaitAsync(Budget);
            Assert.NotSame(ui, loaderContext);
            Assert.Equal(0, ui.PostCount);
            Assert.Equal(1, catalog.SourceCount);
            await catalog.EnsureLoadedAsync(CancellationToken.None);
            Assert.Equal(1, loads);
        }
        finally
        {
            ui.Resume();
        }
    }

    [Fact]
    public async Task FailedInitializationDoesNotPublishAndTheNextQueryCanRetry()
    {
        var loads = 0;
        var catalog = new OnlineSourceCatalog(() =>
        {
            if (Interlocked.Increment(ref loads) == 1)
            {
                throw new InvalidDataException("fixture initial load failed");
            }
            return [new WorkingSource()];
        });

        await Assert.ThrowsAsync<InvalidDataException>(
            () => catalog.EnsureLoadedAsync(CancellationToken.None));
        Assert.Equal(0, catalog.SourceCount);

        var result = await catalog.GetEpisodesAsync(
            new AnimePlaybackContext(42, "Test Anime"), CancellationToken.None);
        Assert.Equal("working", Assert.Single(result).Episode.SourceId);
        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task ConcurrentInitializationPublishesOneCompleteSnapshot()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;
        var catalog = new OnlineSourceCatalog(() =>
        {
            Interlocked.Increment(ref loads);
            started.TrySetResult();
            if (!release.Wait(Budget)) throw new TimeoutException("fixture source loader timed out");
            return [new WorkingSource()];
        });

        var first = catalog.EnsureLoadedAsync(CancellationToken.None);
        Task second = Task.CompletedTask;
        try
        {
            await started.Task.WaitAsync(Budget);
            second = catalog.EnsureLoadedAsync(CancellationToken.None);
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.Equal(0, catalog.SourceCount);
            release.Set();
            await Task.WhenAll(first, second).WaitAsync(Budget);
            Assert.Equal(1, loads);
            Assert.Equal(1, catalog.SourceCount);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(first, second).WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task CancelledInitializationWaitsForTheLoaderAndCanBeRetried()
    {
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;
        var catalog = new OnlineSourceCatalog(() =>
        {
            Interlocked.Increment(ref loads);
            started.TrySetResult();
            if (!release.Wait(Budget)) throw new TimeoutException("fixture source loader timed out");
            return [new WorkingSource()];
        });

        var initialization = catalog.EnsureLoadedAsync(cancellation.Token);
        try
        {
            await started.Task.WaitAsync(Budget);
            cancellation.Cancel();
            Assert.False(initialization.IsCompleted);
        }
        finally
        {
            release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => initialization.WaitAsync(Budget));
        }

        Assert.Equal(0, catalog.SourceCount);
        await catalog.EnsureLoadedAsync(CancellationToken.None).WaitAsync(Budget);
        Assert.Equal(2, loads);
        Assert.Equal(1, catalog.SourceCount);
    }

    [Fact]
    public async Task GetEpisodesAsync_IsolatesFailingSource()
    {
        var catalog = new OnlineSourceCatalog(
        [
            new ThrowingSource(),
            new WorkingSource(),
        ]);

        var episodes = await catalog.GetEpisodesAsync(
            new AnimePlaybackContext(42, "Test Anime"),
            CancellationToken.None);

        var episode = Assert.Single(episodes);
        Assert.Equal("working", episode.Episode.SourceId);
    }

    [Fact]
    public async Task ResolveAsync_DelegatesToOwningSource()
    {
        var catalog = new OnlineSourceCatalog([new WorkingSource()]);
        var episode = new SourceEpisode("working", "episode-1", "Episode 1");

        var media = await catalog.ResolveAsync(
            episode,
            CancellationToken.None);

        Assert.Equal(
            "https://example.test/episode-1.m3u8",
            media.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task ResolveAsync_RecordsSourceFailure()
    {
        var catalog = new OnlineSourceCatalog([new ThrowingSource()]);
        var episode = new SourceEpisode(
            "throwing",
            "episode-1",
            "Episode 1");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => catalog.ResolveAsync(episode, CancellationToken.None));

        Assert.Contains("Throwing 解析失败", exception.Message);
        var diagnostic = Assert.Single(catalog.LastDiagnostics);
        Assert.Equal("解析媒体", diagnostic.Operation);
        Assert.Equal("Source failure", diagnostic.Message);
    }

    [Fact]
    public async Task GetEpisodesAsync_LimitsSourceConcurrencyToFour()
    {
        var probe = new ConcurrencyProbe();
        var catalog = new OnlineSourceCatalog(
            Enumerable.Range(0, 8)
                .Select(index => new GatedSource($"source-{index}", probe)));
        var loading = catalog.GetEpisodesAsync(
            new AnimePlaybackContext(42, "Test Anime"), CancellationToken.None);
        try
        {
            await probe.FourEntered.Task.WaitAsync(Budget);
            Assert.Equal(4, probe.Entered);
            Assert.False(loading.IsCompleted);
            probe.Release.TrySetResult();
            await loading.WaitAsync(Budget);

            Assert.Equal(8, probe.Entered);
            Assert.Equal(4, probe.Maximum);
        }
        finally
        {
            probe.Release.TrySetResult();
            await loading.WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task SelectMappingAsync_RemembersAmbiguousChoice()
    {
        var mappingPath = Path.Combine(
            Path.GetTempPath(),
            $"animeido-mapping-{Guid.NewGuid():N}.json");
        try
        {
            var source = new AmbiguousSource();
            var catalog = new OnlineSourceCatalog(
                [source],
                new SourceMappingStore(mappingPath));
            var anime = new AnimePlaybackContext(42, "Test Anime");

            var first = await catalog.GetEpisodesAsync(
                anime,
                CancellationToken.None);
            var request = Assert.Single(catalog.LastMappingRequests);
            Assert.Empty(first);

            var selected = request.Candidates.Single(candidate =>
                candidate.RemoteId == "remake");
            var selectedEpisodes = await catalog.SelectMappingAsync(
                request,
                selected,
                CancellationToken.None);
            var second = await catalog.GetEpisodesAsync(
                anime,
                CancellationToken.None);

            Assert.Single(selectedEpisodes);
            Assert.Equal(
                "remake",
                Assert.Single(second).Episode.Data!["remoteId"]);
            Assert.Empty(catalog.LastMappingRequests);
        }
        finally
        {
            if (File.Exists(mappingPath))
            {
                File.Delete(mappingPath);
            }
        }
    }

    [Fact]
    public async Task ReloadAsync_ReplacesSourcesForNewQueries()
    {
        IReadOnlyList<IOnlineAnimeSource> sources = [new WorkingSource()];
        var catalog = new OnlineSourceCatalog(() => sources);

        var before = await catalog.GetEpisodesAsync(
            new AnimePlaybackContext(42, "Test Anime"),
            CancellationToken.None);

        sources = [new ReplacementSource()];
        var reload = await catalog.ReloadAsync(CancellationToken.None);
        var after = await catalog.GetEpisodesAsync(
            new AnimePlaybackContext(42, "Test Anime"),
            CancellationToken.None);

        Assert.Equal(1, reload.PreviousCount);
        Assert.Equal(1, reload.CurrentCount);
        Assert.Equal("working", Assert.Single(before).Episode.SourceId);
        Assert.Equal("replacement", Assert.Single(after).Episode.SourceId);
    }

    [Fact]
    public async Task ReloadAsync_PreservesSnapshotWhenFactoryFails()
    {
        var failReload = false;
        var catalog = new OnlineSourceCatalog(() =>
        {
            if (failReload)
            {
                throw new InvalidDataException("Invalid source snapshot");
            }

            return [new WorkingSource()];
        });
        await catalog.EnsureLoadedAsync(CancellationToken.None);
        failReload = true;

        await Assert.ThrowsAsync<InvalidDataException>(
            () => catalog.ReloadAsync(CancellationToken.None));
        var episodes = await catalog.GetEpisodesAsync(
            new AnimePlaybackContext(42, "Test Anime"),
            CancellationToken.None);

        Assert.Equal(1, catalog.SourceCount);
        Assert.Equal("working", Assert.Single(episodes).Episode.SourceId);
    }

    [Fact]
    public async Task ReloadAsync_DoesNotChangeAnInFlightQuerySnapshot()
    {
        var started = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyList<IOnlineAnimeSource> sources =
        [
            new BlockingSource(started, release),
        ];
        var catalog = new OnlineSourceCatalog(() => sources);

        var inFlight = catalog.GetEpisodesAsync(
            new AnimePlaybackContext(42, "Test Anime"),
            CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(Budget);
            sources = [new ReplacementSource()];
            await catalog.ReloadAsync(CancellationToken.None).WaitAsync(Budget);
            release.TrySetResult(true);

            var oldResult = await inFlight.WaitAsync(Budget);
            var newResult = await catalog.GetEpisodesAsync(
                new AnimePlaybackContext(42, "Test Anime"),
                CancellationToken.None);

            Assert.Equal("blocking", Assert.Single(oldResult).Episode.SourceId);
            Assert.Equal("replacement", Assert.Single(newResult).Episode.SourceId);
        }
        finally
        {
            release.TrySetResult(true);
            await inFlight.WaitAsync(Budget);
        }
    }

    private sealed record CatalogLogEntry(
        LogLevel Level, Exception? Exception, IReadOnlyDictionary<string, object?> Properties);

    private sealed class CatalogLogger : ILogger<OnlineSourceCatalog>
    {
        public ConcurrentQueue<CatalogLogEntry> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(item => item.Key, item => item.Value)
                : new Dictionary<string, object?>();
            Entries.Enqueue(new CatalogLogEntry(logLevel, exception, properties));
        }
    }

    private sealed class ErrorSource(Exception failure) : IOnlineAnimeSource
    {
        public string Id => "failing";
        public string DisplayName => "Failing";

        public Task<IReadOnlyList<SourceEpisode>> GetEpisodesAsync(
            AnimePlaybackContext anime, CancellationToken cancellationToken)
            => throw failure;

        public Task<ResolvedMedia> ResolveAsync(SourceEpisode episode, CancellationToken cancellationToken)
            => throw failure;
    }

    private sealed class WorkingSource : IOnlineAnimeSource
    {
        public string Id => "working";

        public string DisplayName => "Working";

        public Task<IReadOnlyList<SourceEpisode>> GetEpisodesAsync(
            AnimePlaybackContext anime,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SourceEpisode>>(
            [
                new SourceEpisode(Id, "episode-1", "Episode 1"),
            ]);

        public Task<ResolvedMedia> ResolveAsync(
            SourceEpisode episode,
            CancellationToken cancellationToken)
            => Task.FromResult(new ResolvedMedia(
                new Uri("https://example.test/episode-1.m3u8"),
                episode.Title,
                new Dictionary<string, string>()));
    }

    private sealed class ThrowingSource : IOnlineAnimeSource
    {
        public string Id => "throwing";

        public string DisplayName => "Throwing";

        public Task<IReadOnlyList<SourceEpisode>> GetEpisodesAsync(
            AnimePlaybackContext anime,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Source failure");

        public Task<ResolvedMedia> ResolveAsync(
            SourceEpisode episode,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Source failure");
    }

    private sealed class ReplacementSource : IOnlineAnimeSource
    {
        public string Id => "replacement";

        public string DisplayName => "Replacement";

        public Task<IReadOnlyList<SourceEpisode>> GetEpisodesAsync(
            AnimePlaybackContext anime,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SourceEpisode>>(
            [
                new SourceEpisode(Id, "episode-1", "Episode 1"),
            ]);

        public Task<ResolvedMedia> ResolveAsync(
            SourceEpisode episode,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class BlockingSource : IOnlineAnimeSource
    {
        private readonly TaskCompletionSource<bool> _started;
        private readonly TaskCompletionSource<bool> _release;

        public BlockingSource(
            TaskCompletionSource<bool> started,
            TaskCompletionSource<bool> release)
        {
            _started = started;
            _release = release;
        }

        public string Id => "blocking";

        public string DisplayName => "Blocking";

        public async Task<IReadOnlyList<SourceEpisode>> GetEpisodesAsync(
            AnimePlaybackContext anime,
            CancellationToken cancellationToken)
        {
            _started.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken);
            return [new SourceEpisode(Id, "episode-1", "Episode 1")];
        }

        public Task<ResolvedMedia> ResolveAsync(
            SourceEpisode episode,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class ConcurrencyProbe
    {
        private int _current;
        private int _maximum;
        private int _entered;

        public int Maximum => Volatile.Read(ref _maximum);
        public int Entered => Volatile.Read(ref _entered);
        public TaskCompletionSource FourEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Enter()
        {
            var current = Interlocked.Increment(ref _current);
            int observed;
            do
            {
                observed = _maximum;
                if (current <= observed)
                {
                    break;
                }
            }
            while (Interlocked.CompareExchange(
                ref _maximum,
                current,
                observed) != observed);
            if (Interlocked.Increment(ref _entered) == 4)
            {
                FourEntered.TrySetResult();
            }
        }

        public void Exit() => Interlocked.Decrement(ref _current);
    }

    private sealed class GatedSource : IOnlineAnimeSource
    {
        private readonly ConcurrencyProbe _probe;

        public GatedSource(string id, ConcurrencyProbe probe)
        {
            Id = id;
            _probe = probe;
        }

        public string Id { get; }

        public string DisplayName => Id;

        public async Task<IReadOnlyList<SourceEpisode>> GetEpisodesAsync(
            AnimePlaybackContext anime,
            CancellationToken cancellationToken)
        {
            _probe.Enter();
            try
            {
                await _probe.Release.Task.WaitAsync(cancellationToken);
                return [];
            }
            finally
            {
                _probe.Exit();
            }
        }

        public Task<ResolvedMedia> ResolveAsync(
            SourceEpisode episode,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class AmbiguousSource : IMappableOnlineAnimeSource
    {
        public string Id => "ambiguous";

        public string DisplayName => "Ambiguous";

        public Task<IReadOnlyList<SourceAnimeCandidate>> SearchAsync(
            AnimePlaybackContext anime,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SourceAnimeCandidate>>(
            [
                new SourceAnimeCandidate(Id, "original", "Test Anime Original"),
                new SourceAnimeCandidate(Id, "remake", "Test Anime Remake"),
            ]);

        public Task<IReadOnlyList<SourceEpisode>> GetEpisodesAsync(
            SourceAnimeCandidate candidate,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SourceEpisode>>(
            [
                new SourceEpisode(
                    Id,
                    "episode-1",
                    "Episode 1",
                    Data: new Dictionary<string, string>
                    {
                        ["remoteId"] = candidate.RemoteId,
                    }),
            ]);

        public async Task<IReadOnlyList<SourceEpisode>> GetEpisodesAsync(
            AnimePlaybackContext anime,
            CancellationToken cancellationToken)
        {
            var candidate = (await SearchAsync(anime, cancellationToken))[0];
            return await GetEpisodesAsync(candidate, cancellationToken);
        }

        public Task<ResolvedMedia> ResolveAsync(
            SourceEpisode episode,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
