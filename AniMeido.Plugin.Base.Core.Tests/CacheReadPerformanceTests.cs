using AniMeido.Plugin.Base.Services;
using System.Diagnostics;
using Xunit.Abstractions;

namespace AniMeido.Tests;

public sealed class CacheReadPerformanceTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly PersonalDatabaseFixture _database = new();

    public Task InitializeAsync() => _database.InitializeAsync();

    public Task DisposeAsync() => _database.DisposeAsync();

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(32)]
    [Trait("Category", "Performance")]
    public async Task ColdReads_ReportApiDispatchAndTotalCostOnWarmDatabase(int count)
    {
        var keys = Enumerable.Range(0, count).Select(index => $"cache-read-{index:D3}").ToArray();
        var payloads = Enumerable.Range(0, count).Select(index => $"payload-{index:D3}:" + new string('x', 4096)).ToArray();
        var writer = new CacheService(_database.Factory);
        for (var index = 0; index < count; index++)
        {
            await writer.SetCacheAsync(keys[index], payloads[index], TimeSpan.FromDays(1));
        }
        var warmup = new CacheService(_database.Factory);
        Assert.Equal(payloads, await Task.WhenAll(keys.Select(warmup.GetCacheAsync)));

        var dispatch = new double[5];
        var total = new double[5];
        var allocated = new long[5];
        for (var iteration = 0; iteration < dispatch.Length; iteration++)
        {
            var reader = new CacheService(_database.Factory);
            var beforeBytes = GC.GetTotalAllocatedBytes(precise: true);
            var started = Stopwatch.GetTimestamp();
            var reads = keys.Select(reader.GetCacheAsync).ToArray();
            dispatch[iteration] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var results = await Task.WhenAll(reads);
            total[iteration] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocated[iteration] = GC.GetTotalAllocatedBytes(precise: true) - beforeBytes;

            Assert.Equal(payloads, results);
        }

        output.WriteLine(
            "Keys={0}; BytesPerPayload={1}; Rounds={2}; MedianApiDispatchMs={3:F3}; MedianTotalMs={4:F3}; MedianProcessAllocatedBytes={5}",
            count, payloads[0].Length, dispatch.Length,
            dispatch.Order().ElementAt(2), total.Order().ElementAt(2), allocated.Order().ElementAt(2));
        output.WriteLine("Warm database; new CacheService per round. Process allocations are comparable only when this class runs alone.");
    }
}
