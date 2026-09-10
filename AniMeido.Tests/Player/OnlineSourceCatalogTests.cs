using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Player.Sources;

namespace AniMeido.Tests;

public sealed class OnlineSourceCatalogTests
{
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
                .Select(index => new DelayedSource(
                    $"source-{index}",
                    probe)));

        await catalog.GetEpisodesAsync(
            new AnimePlaybackContext(42, "Test Anime"),
            CancellationToken.None);

        Assert.InRange(probe.Maximum, 2, 4);
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
                anime,
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
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        sources = [new ReplacementSource()];
        await catalog.ReloadAsync(CancellationToken.None);
        release.SetResult(true);

        var oldResult = await inFlight;
        var newResult = await catalog.GetEpisodesAsync(
            new AnimePlaybackContext(42, "Test Anime"),
            CancellationToken.None);

        Assert.Equal("blocking", Assert.Single(oldResult).Episode.SourceId);
        Assert.Equal(
            "replacement",
            Assert.Single(newResult).Episode.SourceId);
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

        public int Maximum => _maximum;

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
        }

        public void Exit() => Interlocked.Decrement(ref _current);
    }

    private sealed class DelayedSource : IOnlineAnimeSource
    {
        private readonly ConcurrencyProbe _probe;

        public DelayedSource(string id, ConcurrencyProbe probe)
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
                await Task.Delay(40, cancellationToken);
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
