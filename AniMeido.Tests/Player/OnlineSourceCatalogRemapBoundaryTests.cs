using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Player.Playback;
using AniMeido.Plugin.Player.Sources;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class OnlineSourceCatalogRemapBoundaryTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private const string Remap = """[{"seq":1,"from":42,"to":84,"at":"2026-10-28"}]""";

    [Fact]
    public async Task Lookup_MappingWrittenAfterAnAppendedRemapMovesAtTheNextLookup()
    {
        using var paths = new MockAppDataPaths();
        var (logPath, mappingPath) = await SeedAsync(paths);
        var source = new GatedMappingSource();
        var catalog = new OnlineSourceCatalog([source], new SourceMappingStore(mappingPath, remapPath: logPath));
        var loading = catalog.GetEpisodesAsync(new AnimePlaybackContext(42, "Test Anime"), default);
        try
        {
            await source.Entered.Task.WaitAsync(Budget);
            await File.WriteAllTextAsync(logPath, Remap);
            source.Release.TrySetResult();
            var first = await loading.WaitAsync(Budget);
            Assert.Equal("remake", Assert.Single(first).Episode.Data!["remoteId"]);
            var firstSaved = await File.ReadAllTextAsync(mappingPath);

            // 两个候选都不够确定；下一轮只有迁移了刚保存的映射，才能直接返回剧集。
            var next = await catalog.GetEpisodesAsync(
                new AnimePlaybackContext(84, "Test Anime"), default).WaitAsync(Budget);

            Assert.Equal("remake", Assert.Single(next).Episode.Data!["remoteId"]);
            Assert.Empty(catalog.LastMappingRequests);
            using var before = JsonDocument.Parse(firstSaved);
            Assert.Equal(0, before.RootElement.GetProperty("LastRemapSeq").GetInt64());
            Assert.Equal("remake", before.RootElement.GetProperty("Mappings").GetProperty("42:fixture").GetString());
            using var after = JsonDocument.Parse(await File.ReadAllTextAsync(mappingPath));
            Assert.Equal(1, after.RootElement.GetProperty("LastRemapSeq").GetInt64());
            Assert.Equal("remake", after.RootElement.GetProperty("Mappings").GetProperty("84:fixture").GetString());
            Assert.False(after.RootElement.GetProperty("Mappings").TryGetProperty("42:fixture", out _));
        }
        finally
        {
            source.Release.TrySetResult();
            await loading.WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task Lookup_DoesNotReadTheRemapLogAgainWhileMatchingAndSaving()
    {
        using var paths = new MockAppDataPaths();
        var (logPath, mappingPath) = await SeedAsync(paths);
        var source = new GatedMappingSource();
        var catalog = new OnlineSourceCatalog([source], new SourceMappingStore(mappingPath, remapPath: logPath));
        var loading = catalog.GetEpisodesAsync(new AnimePlaybackContext(42, "Test Anime"), default);
        try
        {
            await source.Entered.Task.WaitAsync(Budget);
            using var held = File.Open(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            source.Release.TrySetResult();

            var episodes = await loading.WaitAsync(Budget);

            Assert.Equal("remake", Assert.Single(episodes).Episode.Data!["remoteId"]);
            Assert.Empty(catalog.LastDiagnostics);
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(mappingPath));
            Assert.Equal(0, saved.RootElement.GetProperty("LastRemapSeq").GetInt64());
            Assert.Equal("remake", saved.RootElement.GetProperty("Mappings").GetProperty("42:fixture").GetString());
        }
        finally
        {
            source.Release.TrySetResult();
            await loading.WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task ManualChoice_WrittenAfterAnAppendedRemapMovesAtTheNextLookup()
    {
        using var paths = new MockAppDataPaths();
        var (logPath, mappingPath) = await SeedAsync(paths);
        var source = new GatedMappingSource { FirstSearchIsAmbiguous = true };
        var catalog = new OnlineSourceCatalog([source], new SourceMappingStore(mappingPath, remapPath: logPath));
        var loading = catalog.GetEpisodesAsync(new AnimePlaybackContext(42, "Test Anime"), default);
        try
        {
            await source.Entered.Task.WaitAsync(Budget);
            await File.WriteAllTextAsync(logPath, Remap);
            source.Release.TrySetResult();
            Assert.Empty(await loading.WaitAsync(Budget));
            var request = Assert.Single(catalog.LastMappingRequests);
            var selected = request.Candidates.Single(candidate => candidate.RemoteId == "remake");

            var chosen = await catalog.SelectMappingAsync(request, selected, default).WaitAsync(Budget);
            Assert.Equal("remake", Assert.Single(chosen).Episode.Data!["remoteId"]);
            using var before = JsonDocument.Parse(await File.ReadAllTextAsync(mappingPath));
            Assert.Equal(0, before.RootElement.GetProperty("LastRemapSeq").GetInt64());
            Assert.Equal("remake", before.RootElement.GetProperty("Mappings").GetProperty("42:fixture").GetString());

            var next = await catalog.GetEpisodesAsync(
                new AnimePlaybackContext(84, "Test Anime"), default).WaitAsync(Budget);

            Assert.Equal("remake", Assert.Single(next).Episode.Data!["remoteId"]);
            Assert.Empty(catalog.LastMappingRequests);
            using var after = JsonDocument.Parse(await File.ReadAllTextAsync(mappingPath));
            Assert.Equal(1, after.RootElement.GetProperty("LastRemapSeq").GetInt64());
            Assert.Equal("remake", after.RootElement.GetProperty("Mappings").GetProperty("84:fixture").GetString());
            Assert.False(after.RootElement.GetProperty("Mappings").TryGetProperty("42:fixture", out _));
        }
        finally
        {
            source.Release.TrySetResult();
            await loading.WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task Lookup_ConcurrentSourcesKeepTheSameRemapBoundaryWhileSaving()
    {
        using var paths = new MockAppDataPaths();
        var (logPath, mappingPath) = await SeedAsync(paths);
        var first = new GatedMappingSource("first");
        var second = new GatedMappingSource("second");
        var catalog = new OnlineSourceCatalog([first, second], new SourceMappingStore(mappingPath, remapPath: logPath));
        var loading = catalog.GetEpisodesAsync(new AnimePlaybackContext(42, "Test Anime"), default);
        try
        {
            await Task.WhenAll(first.Entered.Task, second.Entered.Task).WaitAsync(Budget);
            await File.WriteAllTextAsync(logPath, Remap);
            using (var held = File.Open(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                first.Release.TrySetResult();
                second.Release.TrySetResult();
                var episodes = await loading.WaitAsync(Budget);
                Assert.Equal(new[] { "first", "second" },
                    episodes.Select(entry => entry.Episode.SourceId).Order(StringComparer.Ordinal));
                Assert.Empty(catalog.LastDiagnostics);
                using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(mappingPath));
                Assert.Equal(0, saved.RootElement.GetProperty("LastRemapSeq").GetInt64());
                Assert.Equal("remake", saved.RootElement.GetProperty("Mappings").GetProperty("42:first").GetString());
                Assert.Equal("remake", saved.RootElement.GetProperty("Mappings").GetProperty("42:second").GetString());
            }

            var next = await catalog.GetEpisodesAsync(
                new AnimePlaybackContext(84, "Test Anime"), default).WaitAsync(Budget);
            Assert.Equal(2, next.Count);
            Assert.All(next, entry => Assert.Equal("remake", entry.Episode.Data!["remoteId"]));
            Assert.Empty(catalog.LastMappingRequests);
        }
        finally
        {
            first.Release.TrySetResult();
            second.Release.TrySetResult();
            await loading.WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task Stores_ReuseOnePreparedRemapSnapshotForRoutesAndSourceMappings()
    {
        using var paths = new MockAppDataPaths();
        var (logPath, mappingPath) = await SeedAsync(paths);
        var settingsPath = Path.Combine(paths.RootDirectory, "experience.json");
        await File.WriteAllTextAsync(settingsPath,
            """{"SchemaVersion":1,"IdMigrationVersion":9,"LastRemapSeq":0,"PreferredRouteByAnime":{}}""");
        var experience = new PlayerExperienceSettingsStore(settingsPath, remapPath: logPath);
        var mappings = new SourceMappingStore(mappingPath, remapPath: logPath);
        var prepared = await experience.PrepareRouteAsync(42, default).WaitAsync(Budget);
        Assert.Equal(42, prepared.Identity.CurrentAnimeId);
        Assert.Null(prepared.ReadResult.FallbackReason);
        Assert.Equal(0, prepared.ReadResult.Settings.LastRemapSeq);
        await File.WriteAllTextAsync(logPath, Remap);

        using (var held = File.Open(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var lookup = await mappings.BeginLookupAsync(42, prepared.Changes, default).WaitAsync(Budget);
            Assert.Equal(42, lookup.AnimeId);
            await lookup.SetAsync("fixture", "remembered-source", default).WaitAsync(Budget);
            await experience.RecordRouteResultAsync(
                prepared.Identity, "remembered-route", true, TimeSpan.Zero, default).WaitAsync(Budget);

            using var savedMappings = JsonDocument.Parse(await File.ReadAllTextAsync(mappingPath));
            Assert.Equal(0, savedMappings.RootElement.GetProperty("LastRemapSeq").GetInt64());
            Assert.Equal("remembered-source",
                savedMappings.RootElement.GetProperty("Mappings").GetProperty("42:fixture").GetString());
            using var savedSettings = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
            Assert.Equal(0, savedSettings.RootElement.GetProperty("LastRemapSeq").GetInt64());
            Assert.Equal("remembered-route",
                savedSettings.RootElement.GetProperty("PreferredRouteByAnime").GetProperty("42").GetString());
        }

        var next = await experience.PrepareRouteAsync(prepared.Identity, default).WaitAsync(Budget);
        Assert.Equal(84, next.Identity.CurrentAnimeId);
        Assert.Equal(1, next.ReadResult.Settings.LastRemapSeq);
        Assert.Equal("remembered-route", next.ReadResult.Settings.PreferredRouteByAnime["84"]);
        Assert.False(next.ReadResult.Settings.PreferredRouteByAnime.ContainsKey("42"));
        var nextLookup = await mappings.BeginLookupAsync(84, next.Changes, default).WaitAsync(Budget);

        Assert.Equal(84, nextLookup.AnimeId);
        Assert.Equal("remembered-source", await nextLookup.GetAsync("fixture", default).WaitAsync(Budget));
        using var migratedMappings = JsonDocument.Parse(await File.ReadAllTextAsync(mappingPath));
        Assert.Equal(1, migratedMappings.RootElement.GetProperty("LastRemapSeq").GetInt64());
        Assert.Equal("remembered-source",
            migratedMappings.RootElement.GetProperty("Mappings").GetProperty("84:fixture").GetString());
        Assert.False(migratedMappings.RootElement.GetProperty("Mappings").TryGetProperty("42:fixture", out _));
        using var migratedSettings = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
        Assert.Equal(1, migratedSettings.RootElement.GetProperty("LastRemapSeq").GetInt64());
        Assert.Equal("remembered-route",
            migratedSettings.RootElement.GetProperty("PreferredRouteByAnime").GetProperty("84").GetString());
        Assert.False(migratedSettings.RootElement.GetProperty("PreferredRouteByAnime").TryGetProperty("42", out _));
    }

    private static async Task<(string LogPath, string MappingPath)> SeedAsync(MockAppDataPaths paths)
    {
        var logPath = Path.Combine(paths.RootDirectory, "id-remap-log.json");
        var mappingPath = Path.Combine(paths.RootDirectory, "mappings.json");
        await File.WriteAllTextAsync(logPath, "[]");
        await File.WriteAllTextAsync(mappingPath,
            """{"IdMigrationVersion":9,"LastRemapSeq":0,"Mappings":{}}""");
        return (logPath, mappingPath);
    }

    private sealed class GatedMappingSource(string id = "fixture") : IMappableOnlineAnimeSource
    {
        private int _searches;

        public string Id => id;
        public bool FirstSearchIsAmbiguous { get; init; }
        public string DisplayName => "Fixture";
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<SourceAnimeCandidate>> SearchAsync(
            AnimePlaybackContext anime, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _searches) == 1)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                if (!FirstSearchIsAmbiguous)
                    return [new SourceAnimeCandidate(Id, "remake", "Test Anime")];
            }

            return
            [
                new SourceAnimeCandidate(Id, "original", "Test Anime Original"),
                new SourceAnimeCandidate(Id, "remake", "Test Anime Remake"),
            ];
        }

        public Task<IReadOnlyList<SourceEpisode>> GetEpisodesAsync(
            SourceAnimeCandidate candidate, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SourceEpisode>>(
            [
                new SourceEpisode(Id, "episode-1", "Episode 1",
                    Data: new Dictionary<string, string> { ["remoteId"] = candidate.RemoteId }),
            ]);

        public Task<IReadOnlyList<SourceEpisode>> GetEpisodesAsync(
            AnimePlaybackContext anime, CancellationToken cancellationToken)
            => throw new NotSupportedException("Catalog must select a candidate first.");

        public Task<ResolvedMedia> ResolveAsync(SourceEpisode episode, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
