using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

/// <summary>前三项直接显示，其余进入浮层，原顺序与条目引用不变。</summary>
public sealed class TodayPlanStackTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 1, 0)]
    [InlineData(3, 3, 0)]
    [InlineData(4, 3, 1)]
    [InlineData(5, 3, 2)]
    [InlineData(12, 3, 9)]
    public void SplitPlans_KeepsThreeVisibleAndPreservesOrder(int count, int visibleCount, int overflowCount)
    {
        var plans = Plans(Enumerable.Range(1, count).ToArray());
        var (visible, overflow) = TodayViewModel.SplitPlans(plans);
        Assert.Equal(visibleCount, visible.Count);
        Assert.Equal(overflowCount, overflow.Count);
        Assert.Equal(plans, visible.Concat(overflow));
        for (var i = 0; i < count; i++)
            Assert.Same(plans[i], visible.Concat(overflow).ElementAt(i));
    }
    private static IReadOnlyList<TodayPlanEntry> Plans(params int[] animeIds)
        => animeIds
            .Select(id => new TodayPlanEntry(
                new AnimePlan(
                    id,
                    $"作品{id}",
                    AnimePlanPriority.Normal,
                    null,
                    0,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    null,
                    null),
                ""))
            .ToList();
}
