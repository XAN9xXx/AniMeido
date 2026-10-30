using AniMeido.Plugin.Player.Playback;
using AniMeido.Plugin.Player.Sources;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class PlayerPublicIdBoundaryTests
{
    [Fact]
    public async Task OpeningRegistersTheRouteBeforeApplyingPendingRemaps()
    {
        using var fixture = new Fixture();
        await fixture.AppendRemapAsync();

        var opened = await fixture.Boundary.PrepareOpenAsync(42, default);
        var lookup = await fixture.Boundary.PrepareLookupAsync(opened.Identity, default);

        Assert.Equal(84, opened.Identity.CurrentAnimeId);
        Assert.Equal(1, opened.ReadResult.Settings.LastRemapSeq);
        Assert.Equal(84, lookup.Mapping.AnimeId);
        Assert.Equal("source", await lookup.Mapping.GetAsync("site", default));
        Assert.Null(await fixture.Mappings.GetAsync(42, "site", default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsBoundaryPreservesAnActiveLookupsViewAndRoutesItsLateWrites(bool targetExists)
    {
        using var fixture = new Fixture(targetExists);
        var opened = await fixture.Boundary.PrepareOpenAsync(42, default);
        var lookup = await fixture.Boundary.PrepareLookupAsync(opened.Identity, default);
        await fixture.AppendRemapAsync();

        var settings = await fixture.Boundary.PrepareSettingsAsync(default);

        Assert.Equal(84, opened.Identity.CurrentAnimeId);
        Assert.Equal(1, settings.Settings.LastRemapSeq);
        Assert.Equal(42, lookup.Mapping.AnimeId);
        Assert.Equal("source", await lookup.Mapping.GetAsync("site", default));
        await lookup.Mapping.SetAsync("site", "late-source", default);
        await fixture.Experience.RecordRouteResultAsync(opened.Identity,
            "late-route", true, TimeSpan.FromMilliseconds(50), default);
        var next = await fixture.Boundary.PrepareLookupAsync(opened.Identity, default);

        Assert.Equal(84, next.Mapping.AnimeId);
        Assert.Equal(targetExists ? "target" : "late-source", await next.Mapping.GetAsync("site", default));
        Assert.Null(await fixture.Mappings.GetAsync(42, "site", default));
        Assert.Equal(targetExists ? "target-route" : "late-route",
            next.Experience.ReadResult.Settings.PreferredRouteByAnime["84"]);
        Assert.False(next.Experience.ReadResult.Settings.PreferredRouteByAnime.ContainsKey("42"));
        Assert.Equal(1, await fixture.MappingSequenceAsync());
    }

    [Fact]
    public async Task PreparationUsesOneChangeSetForBothStoresRatherThanReopeningTheSourceLog()
    {
        using var fixture = new Fixture(separateSourceLog: true);
        await fixture.AppendRemapAsync();

        var opened = await fixture.Boundary.PrepareOpenAsync(42, default);
        var lookup = await fixture.Boundary.PrepareLookupAsync(opened.Identity, default);

        Assert.Equal(84, opened.Identity.CurrentAnimeId);
        Assert.Equal(84, lookup.Mapping.AnimeId);
        Assert.Equal("source", await lookup.Mapping.GetAsync("site", default));
        Assert.Equal(1, opened.ReadResult.Settings.LastRemapSeq);
        Assert.Equal(1, await fixture.MappingSequenceAsync());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly MockAppDataPaths _paths = new();
        private readonly string _mappingPath;
        public string Log { get; }
        public SourceMappingStore Mappings { get; }
        public PlayerExperienceSettingsStore Experience { get; }
        public PlayerPublicIdBoundary Boundary { get; }

        public Fixture(bool targetExists = false, bool separateSourceLog = false)
        {
            Log = Path.Combine(_paths.RootDirectory, "id-remap-log.json");
            _mappingPath = Path.Combine(_paths.RootDirectory, "mappings.json");
            var experiencePath = Path.Combine(_paths.RootDirectory, "experience.json");
            File.WriteAllText(Log, "[]");
            var mappings = new Dictionary<string, string> { ["42:site"] = "source" };
            var preferred = new Dictionary<string, string> { ["42"] = "source-route" };
            if (targetExists)
            {
                mappings["84:site"] = "target";
                preferred["84"] = "target-route";
            }
            File.WriteAllText(_mappingPath, JsonSerializer.Serialize(new
            {
                IdMigrationVersion = 9,
                LastRemapSeq = 0,
                Mappings = mappings,
            }));
            File.WriteAllText(experiencePath, JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                IdMigrationVersion = 9,
                LastRemapSeq = 0,
                PreferredRouteByAnime = preferred,
            }));
            var sourceLog = Log;
            if (separateSourceLog)
            {
                sourceLog = Path.Combine(_paths.RootDirectory, "unused-source-log.json");
                File.WriteAllText(sourceLog, "broken source log must not be opened");
            }
            Mappings = new SourceMappingStore(_mappingPath, remapPath: sourceLog);
            Experience = new PlayerExperienceSettingsStore(experiencePath, remapPath: Log);
            Boundary = new PlayerPublicIdBoundary(Mappings, Experience);
        }

        public Task AppendRemapAsync() => File.WriteAllTextAsync(Log,
            """[{"seq":1,"from":42,"to":84,"at":"2026-10-28"}]""");

        public async Task<long> MappingSequenceAsync()
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(_mappingPath));
            return json.RootElement.GetProperty("LastRemapSeq").GetInt64();
        }

        public void Dispose() => _paths.Dispose();
    }
}
