using AniMeido.Plugin.Player.Playback;
using System.Diagnostics;
using Xunit.Abstractions;

namespace AniMeido.Tests;

public sealed class PlayerRemapApplyTests
{
    [Fact]
    public void SourceMappings_KeepChainsExistingTargetsSuffixesAndNegativeIds_WithoutChangingInput()
    {
        var input = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["01:site:part"] = "first alias",
            ["+1:site:part"] = "second alias",
            ["1:other"] = "source other",
            ["2:site:part"] = "existing target",
            ["-4:negative:route"] = "negative route",
            ["not-an-id:site"] = "untouched",
            [" 1 :odd"] = "spaced alias",
            ["-6:other"] = "ultimate target",
        };
        var before = input.ToArray();
        PlayerPublicIds.RemapEntry[] entries =
        [
            new(1, 1, 2, "first"),
            new(2, 2, 3, "second"),
            new(3, 3, -6, "third"),
            new(4, -4, -5, "fourth"),
        ];

        var result = PlayerPublicIds.ApplyRemaps(input, entries, sourceKeys: true);

        Assert.NotSame(input, result);
        Assert.Equal(before, input.ToArray());
        Assert.Equal(5, result.Count);
        Assert.Equal("existing target", result["-6:site:part"]);
        Assert.Equal("ultimate target", result["-6:other"]);
        Assert.Equal("negative route", result["-5:negative:route"]);
        Assert.Equal("spaced alias", result["-6:odd"]);
        Assert.Equal("untouched", result["not-an-id:site"]);
    }

    [Fact]
    public void PreferredRoutes_KeepNumericAliasesAndTargets_ButDoNotTreatColonKeysAsSourceMappings()
    {
        var input = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["01"] = "first alias",
            ["+1"] = "second alias",
            ["1"] = "third alias",
            ["3"] = "existing target",
            ["1:source"] = "not a preferred-route ID",
            ["-4"] = "negative route",
        };
        var before = input.ToArray();
        PlayerPublicIds.RemapEntry[] entries =
        [
            new(1, 1, 2, "first"),
            new(2, 2, 3, "second"),
            new(3, -4, -5, "third"),
        ];

        var result = PlayerPublicIds.ApplyRemaps(input, entries, sourceKeys: false);

        Assert.Equal(before, input.ToArray());
        Assert.Equal(3, result.Count);
        Assert.Equal("existing target", result["3"]);
        Assert.Equal("not a preferred-route ID", result["1:source"]);
        Assert.Equal("negative route", result["-5"]);
    }

    [Theory]
    [InlineData(false, "moved")]
    [InlineData(true, "alias")]
    public void AliasCollisionAfterAnEarlierMove_UsesTheCurrentDictionaryOrder(bool aliasFirst, string expected)
    {
        var input = aliasFirst
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["03:a"] = "alias",
                ["1:a"] = "moved",
            }
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["1:a"] = "moved",
                ["03:a"] = "alias",
            };
        var before = input.ToArray();
        PlayerPublicIds.RemapEntry[] entries =
        [
            new(1, 1, 3, "first"),
            new(2, 3, 4, "second"),
        ];

        var result = PlayerPublicIds.ApplyRemaps(input, entries, sourceKeys: true);

        var remaining = Assert.Single(result);
        Assert.Equal("4:a", remaining.Key);
        Assert.Equal(expected, remaining.Value);
        Assert.Equal(before, input.ToArray());
    }

    [Fact]
    public void NoPendingRemaps_ReturnsAnIndependentDictionaryAndPreservesEveryKey()
    {
        var input = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["7:a"] = "route",
            ["legacy text"] = "unchanged",
        };

        var result = PlayerPublicIds.ApplyRemaps(input, [], sourceKeys: true);

        Assert.NotSame(input, result);
        Assert.Equal(input.ToArray(), result.ToArray());
        result.Clear();
        Assert.Equal(2, input.Count);
    }
}

public sealed class PlayerRemapApplyPerformanceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(100, 2)]
    [InlineData(100, 100)]
    [InlineData(1000, 2)]
    [InlineData(1000, 100)]
    [InlineData(5000, 2)]
    [InlineData(5000, 100)]
    public void ApplyMeasuresTheCompleteMappingResult(int mappingCount, int remapCount)
    {
        var input = Enumerable.Range(1, mappingCount).ToDictionary(
            id => $"{id}:source", id => $"route-{id}", StringComparer.Ordinal);
        var before = input.ToArray();
        var entries = Enumerable.Range(1, remapCount)
            .Select(id => new PlayerPublicIds.RemapEntry(id, id, mappingCount + id, "2026-10-27"))
            .ToArray();
        // 夹具只把前 remapCount 个 ID 一对一移到空白区间；预期不调用改写算法。
        var expected = Enumerable.Range(1, mappingCount).ToDictionary(
            id => $"{(id <= remapCount ? mappingCount + id : id)}:source",
            id => $"route-{id}", StringComparer.Ordinal);
        for (var warmup = 0; warmup < 3; warmup++)
        {
            AssertCompleteResult(expected, PlayerPublicIds.ApplyRemaps(input, entries, sourceKeys: true));
        }
        var elapsed = new List<double>(5);
        var allocated = new List<long>(5);
        for (var run = 0; run < 5; run++)
        {
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var started = Stopwatch.GetTimestamp();
            var result = PlayerPublicIds.ApplyRemaps(input, entries, sourceKeys: true);
            elapsed.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            allocated.Add(GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore);
            AssertCompleteResult(expected, result);
        }
        Assert.Equal(before, input.ToArray());

        output.WriteLine("Mappings={0}; remaps={1}; full result; 5-run median={2:F3} ms / {3:N0} B; max={4:F3} ms",
            mappingCount, remapCount, elapsed.Order().ElementAt(2), allocated.Order().ElementAt(2), elapsed.Max());
    }

    private static void AssertCompleteResult(
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var item in expected)
        {
            Assert.True(actual.TryGetValue(item.Key, out var value));
            Assert.Equal(item.Value, value);
        }
    }
}
