using System.Diagnostics;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed partial class MetadataDataSourceTests
{
    [Theory]
    [InlineData(200)]
    [InlineData(2_000)]
    public async Task TagRange_PreservesAllResults_WithLargeCatalog(int count)
    {
        await CreateBaseTablesAsync();
        var briefs = Enumerable.Range(1, count).Select(id => Brief(id, "2026-04-01", 8, id)).ToArray();
        using var handler = TagHandler(briefs);
        using var client = Client(handler);
        var source = Source(client);
        await using var search = new TagSearchResults(source);

        var cold = await search.LoadAsync("科幻", 2025, 2026, default);
        Assert.Equal(count, cold.Count);
        var requests = handler.GetPaths.Count(path => path.StartsWith("/v1/works?ids=", StringComparison.Ordinal));
        var times = new List<double>();
        var allocations = new List<long>();
        for (var run = 0; run < 5; run++)
        {
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var started = Stopwatch.GetTimestamp();
            var results = await search.LoadAsync("科幻", 2025, 2026, default);
            times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            allocations.Add(GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore);
            Assert.Equal(cold.Select(item => item.ID), results.Select(item => item.ID));
        }

        times.Sort();
        allocations.Sort();
        output.WriteLine($"Tag range warm; works={count}; years=2025..2026; runs=5; " +
            $"median={times[2]:F2}ms; allocated median={allocations[2]} bytes; cold work requests={requests}");
    }
}
