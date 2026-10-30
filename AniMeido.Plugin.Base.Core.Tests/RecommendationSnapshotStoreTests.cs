using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Text.Json;
using Xunit.Abstractions;

namespace AniMeido.Tests;

public sealed partial class RecommendationSnapshotStoreTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const string SnapshotKey = "recommendations:metadata:snapshot:v1";
    private readonly TestPaths _paths = new(Path.Combine(Path.GetTempPath(),
        $"animeido-snapshot-tests-{Guid.NewGuid():N}"));
    private SqliteConnectionFactory _factory = null!;
    private CacheService _cache = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_paths.Root);
        _factory = new SqliteConnectionFactory(_paths);
        _cache = new CacheService(_factory);
        await using var connection = await _factory.OpenAsync();
        await PersonalDatabaseSchema.CreateInitialBusinessTablesBeforeConfigAsync(connection);
    }

    [Fact]
    public async Task WriteKeepsTheExistingJsonFormatAndCanBeReadAfterRestart()
    {
        using var store = new RecommendationSnapshotStore(_cache);
        var snapshot = Snapshot();

        Assert.True(await store.TryWriteAsync(snapshot, _cache.CaptureGeneration(),
            store.Revision, CancellationToken.None));

        var json = await _cache.GetCacheAllowExpiredAsync(SnapshotKey);
        Assert.NotNull(json);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(RecommendationSnapshot.CurrentSchemaVersion,
            document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(snapshot.GeneratedAt,
            document.RootElement.GetProperty("generatedAt").GetDateTimeOffset());
        using var restarted = new RecommendationSnapshotStore(new CacheService(_factory));
        var restored = await restarted.ReadAsync(allowExpired: false, CancellationToken.None);
        Assert.NotNull(restored);
        Assert.Equal(snapshot.Items.Select(item => item.Anime.ID), restored.Items.Select(item => item.Anime.ID));
        Assert.Equal(snapshot.HasMore, restored.HasMore);
    }

    [Theory]
    [InlineData(23, true)]
    [InlineData(24, true)]
    [InlineData(25, false)]
    public void FreshnessUsesTheSame24HourBoundary(int ageHours, bool expected)
    {
        var now = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        var snapshot = Snapshot() with { GeneratedAt = now.AddHours(-ageHours) };
        Assert.Equal(expected, RecommendationSnapshotStore.IsFresh(snapshot, now));
        Assert.False(RecommendationSnapshotStore.IsFresh(snapshot with { SchemaVersion = 1 }, now));
    }

    [Fact]
    public async Task ExpiredSnapshotRemainsAvailableOnlyWhenRequested()
    {
        using var store = new RecommendationSnapshotStore(_cache);
        var snapshot = Snapshot() with { GeneratedAt = DateTimeOffset.UtcNow.AddDays(-2) };
        await store.TryWriteAsync(snapshot, _cache.CaptureGeneration(), store.Revision, CancellationToken.None);

        Assert.Null(await store.ReadAsync(allowExpired: false, CancellationToken.None));
        Assert.NotNull(await store.ReadAsync(allowExpired: true, CancellationToken.None));
    }

    [Fact]
    public async Task InvalidationRejectsAnOlderWriteAndAllowsANewRound()
    {
        using var store = new RecommendationSnapshotStore(_cache);
        var revision = store.Revision;
        var generation = _cache.CaptureGeneration();
        await store.TryWriteAsync(Snapshot(), generation, revision, CancellationToken.None);

        await store.InvalidateAsync();

        Assert.False(await store.TryWriteAsync(Snapshot(), generation, revision, CancellationToken.None));
        Assert.Null(await store.ReadAsync(allowExpired: true, CancellationToken.None));
        Assert.True(await store.TryWriteAsync(Snapshot(), generation, store.Revision, CancellationToken.None));
        Assert.NotNull(await store.ReadAsync(allowExpired: false, CancellationToken.None));
    }

    [Fact]
    public async Task ClearingAllCachesDoesNotAllowAnOlderGenerationToRefillTheSnapshot()
    {
        using var store = new RecommendationSnapshotStore(_cache);
        var generation = _cache.CaptureGeneration();
        await _cache.ClearAllCacheAsync();

        await store.TryWriteAsync(Snapshot(), generation, store.Revision, CancellationToken.None);

        Assert.Null(await _cache.GetCacheAllowExpiredAsync(SnapshotKey));
    }

    [Fact]
    public async Task InvalidJsonIsRemovedWhileAnOlderSchemaIsSimplyIgnored()
    {
        using var store = new RecommendationSnapshotStore(_cache);
        await _cache.SetCacheAsync(SnapshotKey, "{", TimeSpan.FromDays(90));
        Assert.Null(await store.ReadAsync(allowExpired: true, CancellationToken.None));
        Assert.Null(await _cache.GetCacheAllowExpiredAsync(SnapshotKey));

        await _cache.SetCacheAsync(SnapshotKey, "{\"schemaVersion\":1}", TimeSpan.FromDays(90));
        Assert.Null(await store.ReadAsync(allowExpired: true, CancellationToken.None));
        Assert.NotNull(await _cache.GetCacheAllowExpiredAsync(SnapshotKey));
    }

    [Fact]
    public async Task PreCanceledReadDoesNotConsumeTheSnapshot()
    {
        using var store = new RecommendationSnapshotStore(_cache);
        await store.TryWriteAsync(Snapshot(), _cache.CaptureGeneration(), store.Revision, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAsync(true, cancellation.Token));
        Assert.NotNull(await store.ReadAsync(allowExpired: false, CancellationToken.None));
    }

    [Fact]
    public async Task RoundIdentityRequiresTheCurrentSchemaAndExactGenerationTime()
    {
        using var store = new RecommendationSnapshotStore(_cache);
        var snapshot = Snapshot();
        Assert.False(await store.MatchesRoundAsync(snapshot.GeneratedAt, CancellationToken.None));
        await store.TryWriteAsync(snapshot, _cache.CaptureGeneration(), store.Revision, CancellationToken.None);

        Assert.True(await store.MatchesRoundAsync(snapshot.GeneratedAt, CancellationToken.None));
        Assert.False(await store.MatchesRoundAsync(snapshot.GeneratedAt.AddTicks(1), CancellationToken.None));
        await store.TryWriteAsync(snapshot with { SchemaVersion = 1 }, _cache.CaptureGeneration(),
            store.Revision, CancellationToken.None);
        Assert.False(await store.MatchesRoundAsync(snapshot.GeneratedAt, CancellationToken.None));
    }

    [Fact]
    public async Task RoundIdentityAllowsAnExpiredRoundButRemovesMalformedJson()
    {
        using var store = new RecommendationSnapshotStore(_cache);
        var snapshot = Snapshot() with { GeneratedAt = DateTimeOffset.UtcNow.AddDays(-2) };
        await store.TryWriteAsync(snapshot, _cache.CaptureGeneration(), store.Revision, CancellationToken.None);
        Assert.True(await store.MatchesRoundAsync(snapshot.GeneratedAt, CancellationToken.None));

        await _cache.SetCacheAsync(SnapshotKey, "{", TimeSpan.FromDays(90));
        Assert.False(await store.MatchesRoundAsync(snapshot.GeneratedAt, CancellationToken.None));
        Assert.Null(await _cache.GetCacheAllowExpiredAsync(SnapshotKey));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(2000)]
    public async Task RoundIdentityAvoidsReconstructingPreviouslyShownWorks(int count)
    {
        using var store = new RecommendationSnapshotStore(_cache);
        var snapshot = Snapshot(count);
        await store.TryWriteAsync(snapshot, _cache.CaptureGeneration(), store.Revision, CancellationToken.None);
        var full = await MeasureAsync(() => store.ReadAsync(true, CancellationToken.None));
        var identity = await MeasureAsync(() => store.MatchesRoundAsync(snapshot.GeneratedAt, CancellationToken.None));

        Assert.True(identity.Allocated < full.Allocated);
        Assert.True(await store.MatchesRoundAsync(snapshot.GeneratedAt, CancellationToken.None));
        Assert.Equal(count, (await store.ReadAsync(true, CancellationToken.None))!.Items.Count);
        output.WriteLine("{0}; Release x64; {1}; warm cache; 5-run medians", count,
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        output.WriteLine("full: {0:F3} ms / {1} B; identity: {2:F3} ms / {3} B",
            full.Milliseconds, full.Allocated, identity.Milliseconds, identity.Allocated);
    }

    private static async Task<(double Milliseconds, long Allocated)> MeasureAsync(Func<Task> action)
    {
        await action();
        await action();
        var elapsed = new double[5];
        var allocated = new long[5];
        for (var i = 0; i < elapsed.Length; i++)
        {
            var thread = Environment.CurrentManagedThreadId;
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            await action();
            elapsed[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocated[i] = GC.GetAllocatedBytesForCurrentThread() - bytes;
            // 热内存路径同步完成；跨线程的分配数字不能拿来作比较。
            Assert.Equal(thread, Environment.CurrentManagedThreadId);
        }
        Array.Sort(elapsed);
        Array.Sort(allocated);
        return (elapsed[2], allocated[2]);
    }

    private static RecommendationSnapshot Snapshot(int count = 1)
    {
        var items = Enumerable.Range(42, count).Select(id =>
        {
            var anime = new Anime(id, "测试作品", null, [], new DateOnly(2026, 1, 1),
                null, string.Empty, 2026, 1, Score: 8);
            return new RecommendationItem(anime, 2.5, [], true, true);
        }).ToArray();
        return new RecommendationSnapshot(RecommendationSnapshot.CurrentSchemaVersion, DateTimeOffset.UtcNow,
            true, items, HasMore: true);
    }

    public Task DisposeAsync()
    {
        using var connection = _factory.CreateConnection();
        SqliteConnection.ClearPool(connection);
        Directory.Delete(_paths.Root, recursive: true);
        return Task.CompletedTask;
    }

    private sealed record TestPaths(string Root) : IAppDataPaths
    {
        public string DatabasePath => Path.Combine(Root, "test.db");
        public string BackupDirectory => Path.Combine(Root, "Backups");
        public string LogDirectory => Path.Combine(Root, "Logs");
    }
}
