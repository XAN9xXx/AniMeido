using AniMeido.Plugin.Player.Playback;
using AniMeido.Plugin.Player.Sources;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class PlayerExperienceRemapBoundaryTests
{
    [Fact]
    public async Task OrdinaryReadsAndWritesDoNotReadPendingRemaps()
    {
        using var paths = new MockAppDataPaths();
        var (path, log) = await SeedAsync(paths.RootDirectory);
        var store = new PlayerExperienceSettingsStore(path, remapPath: log);
        using (var held = File.Open(log, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var read = await store.ReadAsync(default);
            Assert.Null(read.FallbackReason);
            var identity = await store.BeginRouteAsync(42, default);
            await store.UpdatePreferencesAsync(65, false, 1.25, true, 1280, 720, default);
            await store.RecordRouteResultAsync(42, "legacy-route", true, TimeSpan.Zero, default);
            await store.RecordRouteResultAsync(identity, "current-route", true, TimeSpan.Zero, default);
            var unchanged = (await store.ReadAsync(default)).Settings;
            Assert.Equal(0, unchanged.LastRemapSeq);
            Assert.Equal("current-route", unchanged.PreferredRouteByAnime["42"]);
            Assert.False(unchanged.PreferredRouteByAnime.ContainsKey("43"));
        }

        var migrated = (await store.ReadAndApplyPublicIdsAsync(default)).Settings;
        Assert.Equal(1, migrated.LastRemapSeq);
        Assert.Equal("current-route", migrated.PreferredRouteByAnime["43"]);
        Assert.False(migrated.PreferredRouteByAnime.ContainsKey("42"));
        Assert.Equal(65, migrated.Volume);
    }

    [Fact]
    public async Task APreviousLookupIdentityWritesUnderTheAppliedIdWithoutReadingTheLogAgain()
    {
        using var paths = new MockAppDataPaths();
        var (path, log) = await SeedAsync(paths.RootDirectory, pending: false);
        var store = new PlayerExperienceSettingsStore(path, remapPath: log);
        await store.ReadAndApplyPublicIdsAsync(default);
        var identity = await store.BeginRouteAsync(42, default);
        await AppendRemapAsync(log);
        await store.ReadAndApplyPublicIdsAsync(default);

        using (var held = File.Open(log, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await store.RecordRouteResultAsync(identity, "late-route", true, TimeSpan.FromMilliseconds(20), default);
        }

        var actual = (await store.ReadAsync(default)).Settings;
        Assert.Equal(42, identity.OriginalAnimeId);
        Assert.Equal(1, actual.LastRemapSeq);
        Assert.Equal("late-route", actual.PreferredRouteByAnime["43"]);
        Assert.False(actual.PreferredRouteByAnime.ContainsKey("42"));
        Assert.Equal(1, actual.RouteHealth["late-route"].SuccessCount);
    }

    [Fact]
    public async Task TheTargetPreferenceWinsOverAnOldLookupButRouteHealthStillAccumulates()
    {
        using var paths = new MockAppDataPaths();
        var (path, log) = await SeedAsync(paths.RootDirectory, pending: false, target: true);
        var store = new PlayerExperienceSettingsStore(path, remapPath: log);
        await store.ReadAndApplyPublicIdsAsync(default);
        var oldIdentity = await store.BeginRouteAsync(42, default);
        await AppendRemapAsync(log);
        await store.ReadAndApplyPublicIdsAsync(default);
        await store.RecordRouteResultAsync(oldIdentity, "old-lookup-route", true, TimeSpan.Zero, default);

        var actual = (await store.ReadAsync(default)).Settings;
        Assert.Equal("target-route", actual.PreferredRouteByAnime["43"]);
        Assert.False(actual.PreferredRouteByAnime.ContainsKey("42"));
        Assert.Equal(1, actual.RouteHealth["old-lookup-route"].SuccessCount);
        var currentIdentity = await store.BeginRouteAsync(43, default);
        await store.RecordRouteResultAsync(currentIdentity, "new-lookup-route", true, TimeSpan.Zero, default);
        Assert.Equal("new-lookup-route", (await store.ReadAsync(default)).Settings.PreferredRouteByAnime["43"]);
    }

    [Fact]
    public async Task AFailedRemapCommitDoesNotPublishTheIdentityChangeOrShadowItsWrites()
    {
        using var paths = new MockAppDataPaths();
        var (path, log) = await SeedAsync(paths.RootDirectory, pending: false, target: true);
        var original = await File.ReadAllTextAsync(path);
        var store = new PlayerExperienceSettingsStore(path, remapPath: log);
        await store.ReadAndApplyPublicIdsAsync(default);
        var identity = await store.BeginRouteAsync(42, default);
        await AppendRemapAsync(log);
        using (var held = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() => store.ReadAndApplyPublicIdsAsync(default));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Equal(original, await File.ReadAllTextAsync(path));
        }

        await store.RecordRouteResultAsync(identity, "still-old-route", true, TimeSpan.Zero, default);
        var unchanged = (await store.ReadAsync(default)).Settings;
        Assert.Equal(0, unchanged.LastRemapSeq);
        Assert.Equal("still-old-route", unchanged.PreferredRouteByAnime["42"]);
        await store.ReadAndApplyPublicIdsAsync(default);
        await store.RecordRouteResultAsync(identity, "shadowed-route", true, TimeSpan.Zero, default);
        var migrated = (await store.ReadAsync(default)).Settings;
        Assert.Equal(1, migrated.LastRemapSeq);
        Assert.False(migrated.PreferredRouteByAnime.ContainsKey("42"));
        Assert.Equal("target-route", migrated.PreferredRouteByAnime["43"]);
        Assert.Equal(1, migrated.RouteHealth["shadowed-route"].SuccessCount);
        Assert.Empty(Directory.EnumerateFiles(paths.RootDirectory, "*.tmp"));
    }

    [Fact]
    public async Task FirstOpenPreparationResolvesPendingChangesBeforeReturningIdentity()
    {
        using var paths = new MockAppDataPaths();
        var (path, log) = await SeedAsync(paths.RootDirectory);
        var store = new PlayerExperienceSettingsStore(path, remapPath: log);

        var prepared = await store.PrepareRouteAsync(42, default);

        Assert.Equal(42, prepared.Identity.OriginalAnimeId);
        Assert.Equal(43, prepared.Identity.CurrentAnimeId);
        Assert.Null(prepared.ReadResult.FallbackReason);
        Assert.Equal(1, prepared.ReadResult.Settings.LastRemapSeq);
        using (var held = File.Open(log, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await store.RecordRouteResultAsync(prepared.Identity, "first-open-route", true, TimeSpan.Zero, default);
        }
        var actual = (await store.ReadAsync(default)).Settings;
        Assert.Equal(1, actual.LastRemapSeq);
        Assert.False(actual.PreferredRouteByAnime.ContainsKey("42"));
        Assert.Equal("first-open-route", actual.PreferredRouteByAnime["43"]);
    }

    [Fact]
    public async Task OpeningCanRetryAfterOnlyExperienceCommittedAndStillUseTheCurrentId()
    {
        using var paths = new MockAppDataPaths();
        var (path, log) = await SeedAsync(paths.RootDirectory);
        var experience = new PlayerExperienceSettingsStore(path, remapPath: log);
        var sourcePath = Path.Combine(paths.RootDirectory, "mappings.json");
        await File.WriteAllTextAsync(sourcePath,
            """{"IdMigrationVersion":9,"Mappings":{"42:site":"remembered"}}""");
        var mappings = new SourceMappingStore(sourcePath, remapPath: log);
        var boundary = new PlayerPublicIdBoundary(mappings, experience);
        using (var held = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() => boundary.PrepareOpenAsync(42, default));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Equal(1, (await experience.ReadAsync(default)).Settings.LastRemapSeq);
        }

        var opening = await boundary.PrepareOpenAsync(42, default);
        Assert.Equal(42, opening.Identity.OriginalAnimeId);
        Assert.Equal(43, opening.Identity.CurrentAnimeId);
        var lookup = await boundary.PrepareLookupAsync(opening.Identity, default);
        Assert.Equal(43, lookup.Mapping.AnimeId);
        Assert.Equal("remembered", await lookup.Mapping.GetAsync("site", default));
        await lookup.Mapping.SetAsync("site", "retry-source", default);
        await experience.RecordRouteResultAsync(opening.Identity, "retry-route", true, TimeSpan.Zero, default);

        var saved = (await experience.ReadAsync(default)).Settings;
        Assert.Equal(1, saved.LastRemapSeq);
        Assert.False(saved.PreferredRouteByAnime.ContainsKey("42"));
        Assert.Equal("retry-route", saved.PreferredRouteByAnime["43"]);
        Assert.Null(await mappings.GetAsync(42, "site", default));
        Assert.Equal("retry-source", await mappings.GetAsync(43, "site", default));
    }

    [Fact]
    public async Task InvalidExperienceStillLetsTheSourceLookupUseTheCurrentIdWithoutOverwritingIt()
    {
        using var paths = new MockAppDataPaths();
        var (path, log) = await SeedAsync(paths.RootDirectory);
        const string original = "{broken-json";
        await File.WriteAllTextAsync(path, original);
        var experience = new PlayerExperienceSettingsStore(path, remapPath: log);
        var sourcePath = Path.Combine(paths.RootDirectory, "mappings.json");
        await File.WriteAllTextAsync(sourcePath,
            """{"IdMigrationVersion":9,"Mappings":{"42:site":"remembered"}}""");
        var mappings = new SourceMappingStore(sourcePath, remapPath: log);
        var boundary = new PlayerPublicIdBoundary(mappings, experience);

        var opening = await boundary.PrepareOpenAsync(42, default);
        Assert.IsAssignableFrom<JsonException>(opening.ReadResult.FallbackReason);
        var lookup = await boundary.PrepareLookupAsync(opening.Identity, default);

        Assert.Equal(43, lookup.Mapping.AnimeId);
        Assert.Equal("remembered", await lookup.Mapping.GetAsync("site", default));
        await Assert.ThrowsAsync<JsonException>(() => experience.RecordRouteResultAsync(
            opening.Identity, "failed-save-route", true, TimeSpan.Zero, default));
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Null(await mappings.GetAsync(42, "site", default));
        Assert.Equal("remembered", await mappings.GetAsync(43, "site", default));
    }

    [Fact]
    public async Task RepairingBadSettingsDoesNotReplayConsumedIdentityChanges()
    {
        using var paths = new MockAppDataPaths();
        var (path, log) = await SeedAsync(paths.RootDirectory, pending: false);
        await File.WriteAllTextAsync(path, "{broken-json");
        await File.WriteAllTextAsync(log,
            """[{"seq":1,"from":2,"to":3,"at":"2026-10-28"},{"seq":2,"from":1,"to":2,"at":"2026-10-28"}]""");
        var store = new PlayerExperienceSettingsStore(path, remapPath: log);
        var opening = await store.PrepareRouteAsync(1, default);
        Assert.IsAssignableFrom<JsonException>(opening.ReadResult.FallbackReason);
        Assert.Equal(2, opening.Identity.CurrentAnimeId);
        var repaired = new PlayerExperienceSettings { IdMigrationVersion = 9 };
        repaired.PreferredRouteByAnime["1"] = "original-route";
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(repaired));

        await store.ReadAndApplyPublicIdsAsync(default);
        await store.RecordRouteResultAsync(opening.Identity, "continued-route", true, TimeSpan.Zero, default);

        var actual = (await store.ReadAsync(default)).Settings;
        Assert.Equal(2, opening.Identity.CurrentAnimeId);
        Assert.Equal(2, actual.LastRemapSeq);
        Assert.False(actual.PreferredRouteByAnime.ContainsKey("1"));
        Assert.False(actual.PreferredRouteByAnime.ContainsKey("3"));
        Assert.Equal("continued-route", actual.PreferredRouteByAnime["2"]);
    }

    [Fact]
    public async Task RepairingLegacySettingsDoesNotReinterpretAPublicIdentityAsABangumiId()
    {
        using var paths = new MockAppDataPaths();
        var experiencePath = Path.Combine(paths.RootDirectory, "experience.json");
        var mappingPath = Path.Combine(paths.RootDirectory, "mappings.json");
        var migration = Path.Combine(paths.RootDirectory, "migration.json");
        var log = Path.Combine(paths.RootDirectory, "remaps.json");
        await File.WriteAllTextAsync(experiencePath, "{broken-json");
        await File.WriteAllTextAsync(mappingPath,
            """{"IdMigrationVersion":9,"Mappings":{"42:site":"source"}}""");
        await File.WriteAllTextAsync(migration, """{"schemaVersion":9,"workIds":{"7":42}}""");
        await File.WriteAllTextAsync(log, "[]");
        var store = new PlayerExperienceSettingsStore(experiencePath, migration, log);
        var boundary = new PlayerPublicIdBoundary(new SourceMappingStore(mappingPath, migration, log), store);
        var opened = await boundary.PrepareOpenAsync(42, default);
        Assert.Equal(42, opened.Identity.CurrentAnimeId);
        await File.WriteAllTextAsync(experiencePath,
            """{"SchemaVersion":1,"PreferredRouteByAnime":{"7":"legacy-route"}}""");

        await boundary.PrepareSettingsAsync(default);

        Assert.Equal(42, opened.Identity.CurrentAnimeId);
        await store.RecordRouteResultAsync(opened.Identity, "new-route", true, TimeSpan.Zero, default);
        var actual = (await store.ReadAsync(default)).Settings;
        Assert.Equal("new-route", actual.PreferredRouteByAnime["42"]);
        Assert.False(actual.PreferredRouteByAnime.ContainsKey("-42"));
    }

    private static async Task<(string Settings, string Log)> SeedAsync(
        string root, bool pending = true, bool target = false)
    {
        var path = Path.Combine(root, "experience.json");
        var log = Path.Combine(root, "id-remap-log.json");
        var settings = new PlayerExperienceSettings { IdMigrationVersion = 9 };
        settings.PreferredRouteByAnime["42"] = "old-route";
        if (target)
            settings.PreferredRouteByAnime["43"] = "target-route";
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(settings));
        await File.WriteAllTextAsync(log, "[]");
        if (pending)
            await AppendRemapAsync(log);
        return (path, log);
    }

    private static Task AppendRemapAsync(string log)
        => File.WriteAllTextAsync(log, """[{"seq":1,"from":42,"to":43,"at":"2026-10-28"}]""");
}
