using AniMeido.Plugin.Player.Playback;
using System.Diagnostics;
using System.Text.Json;
using Xunit.Abstractions;

namespace AniMeido.Tests;

public sealed class PlayerRemapLogReadTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0, new long[] { 1, 2 })]
    [InlineData(1, new long[] { 2 })]
    [InlineData(2, new long[0])]
    [InlineData(9, new long[0])]
    public async Task IncrementalLogReadReturnsOnlyNewerRows(long afterSequence, long[] expected)
    {
        using var paths = new MockAppDataPaths();
        var path = Path.Combine(paths.RootDirectory, "id-remap-log.json");
        await File.WriteAllTextAsync(path,
            """[{"seq":1,"from":-1,"to":2,"at":"2026-10-26"},{"seq":2,"from":2,"to":3,"at":"2026-10-26"}]""");

        var result = await PlayerPublicIds.ReadRemapsAsync(path, afterSequence, default);
        Assert.Equal(expected, result.Select(row => row.Seq));
    }

    [Fact]
    public async Task InvalidConsumedEntryStillRejectsTheLog()
    {
        using var paths = new MockAppDataPaths();
        var path = Path.Combine(paths.RootDirectory, "id-remap-log.json");
        await File.WriteAllTextAsync(path,
            """[{"seq":1,"from":2,"to":2,"at":"2026-10-26"},{"seq":2,"from":2,"to":3,"at":"2026-10-26"}]""");

        await Assert.ThrowsAsync<InvalidDataException>(() => PlayerPublicIds.ReadRemapsAsync(path, 1, default));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[{\"seq\":2,\"from\":1,\"to\":2,\"at\":\"2026-10-26\"}]")]
    [InlineData("[{\"seq\":1,\"from\":1,\"to\":2,\"at\":\"2026-10-26\"},")]
    public async Task InvalidHistoryIsNotAnEmptyIncrementalResult(string text)
    {
        using var paths = new MockAppDataPaths();
        var path = Path.Combine(paths.RootDirectory, "id-remap-log.json");
        await File.WriteAllTextAsync(path, text);

        Assert.NotNull(await Record.ExceptionAsync(() => PlayerPublicIds.ReadRemapsAsync(path, 9, default)));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(5000)]
    public async Task ReadMostlyConsumedLogMeasuresTheSameCompleteHistory(int count)
    {
        using var paths = new MockAppDataPaths();
        var path = Path.Combine(paths.RootDirectory, "id-remap-log.json");
        var rows = Enumerable.Range(1, count)
            .Select(seq => new PlayerPublicIds.RemapEntry(seq, seq, seq + 1, "2026-10-26"));
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(rows, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var afterSequence = count - 2;
        Assert.Equal(2, (await ReadAfterAsync(path, afterSequence)).Count);
        var elapsed = new List<double>();
        var allocated = new List<long>();
        for (var run = 0; run < 5; run++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var started = Stopwatch.GetTimestamp();
            var result = await ReadAfterAsync(path, afterSequence);
            elapsed.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            allocated.Add(GC.GetTotalAllocatedBytes(precise: true) - before);
            Assert.Equal(new long[] { count - 1, count }, result.Select(row => row.Seq));
        }

        output.WriteLine("History={0}; pending=2; warm file reads; 5-run median={1:F3} ms / {2:N0} B",
            count, elapsed.Order().ElementAt(2), allocated.Order().ElementAt(2));
    }

    private static Task<List<PlayerPublicIds.RemapEntry>> ReadAfterAsync(string path, long afterSequence)
        => PlayerPublicIds.ReadRemapsAsync(path, afterSequence, default);
}
