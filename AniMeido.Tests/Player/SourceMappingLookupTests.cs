using AniMeido.Plugin.Player.Sources;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class SourceMappingLookupTests
{
    [Fact]
    public async Task ALateLegacyMigrationDoesNotChangeALookupsPublicWriteIdentity()
    {
        using var paths = new MockAppDataPaths();
        var path = Path.Combine(paths.RootDirectory, "mappings.json");
        var migration = Path.Combine(paths.RootDirectory, "migration.json");
        var store = new SourceMappingStore(path, migration);
        var lookup = await store.BeginLookupAsync(42, default);
        await File.WriteAllTextAsync(migration, """{"schemaVersion":9,"workIds":{"7":42}}""");

        await store.EnsurePublicIdsAsync(default);
        await lookup.SetAsync("site", "new-route", default);

        Assert.Equal(42, lookup.AnimeId);
        Assert.Equal("new-route", await store.GetAsync(42, "site", default));
        Assert.Null(await store.GetAsync(-42, "site", default));
    }

    [Fact]
    public async Task LookupWriteBeforeTheNextBoundaryMigratesWithTheNewRemap()
    {
        using var fixture = new Fixture();
        await fixture.Store.SetAsync(1, "site", "original", default);
        var lookup = await fixture.Store.BeginLookupAsync(1, default);
        await fixture.AppendRemapAsync();

        await lookup.SetAsync("site", "during-lookup", default);

        Assert.Equal("during-lookup", await fixture.Store.GetAsync(1, "site", default));
        Assert.Null(await fixture.Store.GetAsync(2, "site", default));
        var next = await fixture.Store.BeginLookupAsync(2, default);
        Assert.Equal("during-lookup", await next.GetAsync("site", default));
        Assert.Null(await fixture.Store.GetAsync(1, "site", default));
        Assert.Equal(1, await fixture.LastSequenceAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OlderLookupWritesUseTheCommittedRouteAndRespectAnExistingTarget(bool targetExists)
    {
        using var fixture = new Fixture();
        await fixture.Store.SetAsync(1, "site", "source", default);
        if (targetExists)
            await fixture.Store.SetAsync(2, "site", "target", default);
        var older = await fixture.Store.BeginLookupAsync(1, default);
        await fixture.AppendRemapAsync();
        var newer = await fixture.Store.BeginLookupAsync(2, default);
        Assert.Equal(targetExists ? "target" : "source", await newer.GetAsync("site", default));
        Assert.Equal("source", await older.GetAsync("site", default));

        await older.SetAsync("site", "late-source", default);

        Assert.Equal("late-source", await older.GetAsync("site", default));
        Assert.Equal(targetExists ? "target" : "late-source", await fixture.Store.GetAsync(2, "site", default));
        Assert.Null(await fixture.Store.GetAsync(1, "site", default));
        Assert.Equal(1, await fixture.LastSequenceAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OlderLookupRemovalDoesNotDeleteAnExistingRemapTarget(bool targetExists)
    {
        using var fixture = new Fixture();
        await fixture.Store.SetAsync(1, "site", "source", default);
        if (targetExists)
            await fixture.Store.SetAsync(2, "site", "target", default);
        var older = await fixture.Store.BeginLookupAsync(1, default);
        await fixture.AppendRemapAsync();
        await fixture.Store.EnsurePublicIdsAsync(default);

        await older.RemoveAsync("site", default);

        Assert.Null(await older.GetAsync("site", default));
        Assert.Null(await fixture.Store.GetAsync(1, "site", default));
        Assert.Equal(targetExists ? "target" : null, await fixture.Store.GetAsync(2, "site", default));
        Assert.Equal(1, await fixture.LastSequenceAsync());
    }

    [Fact]
    public async Task LookupReadsStayFixedWhileOtherOperationsChangeTheSameMapping()
    {
        using var fixture = new Fixture();
        await fixture.Store.SetAsync(1, "site", "before", default);
        var lookup = await fixture.Store.BeginLookupAsync(1, default);
        await fixture.Store.SetAsync(1, "site", "outside", default);

        Assert.Equal("before", await lookup.GetAsync("site", default));
        await lookup.SetAsync("site", "inside", default);
        Assert.Equal("inside", await lookup.GetAsync("site", default));
        Assert.Equal("inside", await fixture.Store.GetAsync(1, "site", default));
    }

    [Fact]
    public async Task OrdinaryReadsAndWritesDoNotReopenTheRemapLog()
    {
        using var fixture = new Fixture();
        await fixture.Store.EnsurePublicIdsAsync(default);
        var lookup = await fixture.Store.BeginLookupAsync(1, default);
        using var held = File.Open(fixture.Log, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        await fixture.Store.SetAsync(1, "outside", "outside", default);
        Assert.Equal("outside", await fixture.Store.GetAsync(1, "outside", default));
        await fixture.Store.RemoveAsync(1, "outside", default);
        await lookup.SetAsync("site", "inside", default);
        Assert.Equal("inside", await lookup.GetAsync("site", default));
        await lookup.RemoveAsync("site", default);
        Assert.Null(await lookup.GetAsync("site", default));
    }

    [Fact]
    public async Task FailedMigrationDoesNotAdvanceAnOlderLookupsWriteRoute()
    {
        using var fixture = new Fixture();
        await fixture.Store.SetAsync(1, "site", "source", default);
        var lookup = await fixture.Store.BeginLookupAsync(1, default);
        await fixture.AppendRemapAsync();
        using (var held = File.Open(fixture.Mappings, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() => fixture.Store.EnsurePublicIdsAsync(default));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }

        await lookup.SetAsync("site", "after-failure", default);

        Assert.Equal("after-failure", await fixture.Store.GetAsync(1, "site", default));
        Assert.Null(await fixture.Store.GetAsync(2, "site", default));
        var next = await fixture.Store.BeginLookupAsync(2, default);
        Assert.Equal("after-failure", await next.GetAsync("site", default));
        Assert.Null(await fixture.Store.GetAsync(1, "site", default));
    }

    [Fact]
    public async Task LookupStartedWithTheOldInputFollowsRemapsAppliedAtItsOwnBoundary()
    {
        using var fixture = new Fixture();
        await fixture.Store.SetAsync(1, "site", "source", default);
        await fixture.AppendRemapAsync();

        var lookup = await fixture.Store.BeginLookupAsync(1, default);

        Assert.Equal("source", await lookup.GetAsync("site", default));
        await lookup.SetAsync("site", "current", default);
        Assert.Equal("current", await fixture.Store.GetAsync(2, "site", default));
        Assert.Null(await fixture.Store.GetAsync(1, "site", default));
    }

    [Fact]
    public async Task FailedLookupSaveDoesNotPublishTheNewSnapshotValue()
    {
        using var fixture = new Fixture();
        await fixture.Store.SetAsync(1, "site", "source", default);
        var lookup = await fixture.Store.BeginLookupAsync(1, default);
        using (var held = File.Open(fixture.Mappings, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() => lookup.SetAsync("site", "unsaved", default));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Equal("source", await lookup.GetAsync("site", default));
            Assert.Equal("source", await fixture.Store.GetAsync(1, "site", default));
        }
        await lookup.SetAsync("site", "saved", default);
        Assert.Equal("saved", await lookup.GetAsync("site", default));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly MockAppDataPaths _paths = new();
        public string Log { get; }
        public string Mappings { get; }
        public SourceMappingStore Store { get; }

        public Fixture()
        {
            Log = Path.Combine(_paths.RootDirectory, "id-remap-log.json");
            Mappings = Path.Combine(_paths.RootDirectory, "mappings.json");
            File.WriteAllText(Log, "[]");
            Store = new SourceMappingStore(Mappings, remapPath: Log);
        }

        public Task AppendRemapAsync() => File.WriteAllTextAsync(Log,
            """[{"seq":1,"from":1,"to":2,"at":"2026-10-28"}]""");

        public async Task<long> LastSequenceAsync()
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Mappings));
            return json.RootElement.GetProperty("LastRemapSeq").GetInt64();
        }

        public void Dispose() => _paths.Dispose();
    }
}
