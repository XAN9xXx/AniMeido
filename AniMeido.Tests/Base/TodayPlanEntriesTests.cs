using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

public sealed class TodayPlanEntriesTests
{
    private static readonly DateOnly Today = new(2026, 9, 13);

    [Fact]
    public void Plans_AreGroupedByUrgencyWithHeaderOnFirstEntryOnly()
    {
        var entries = TodayViewModel.BuildPlanEntries(
            [
                Plan(1, null),
                Plan(2, Today.AddDays(-3)),
                Plan(3, Today.AddDays(3)),
                Plan(4, Today.AddDays(30)),
                Plan(5, null),
            ],
            new Dictionary<int, int>(),
            new Dictionary<int, Anime>(),
            Today);

        Assert.Equal(
            new[] { 2, 3, 4, 1, 5 },
            entries.Select(entry => entry.Plan.AnimeId));
        Assert.Equal(
            new[] { "已逾期 · 1", "近期 · 七天内 · 1", "之后 · 1", "未排期 · 2", "" },
            entries.Select(entry => entry.GroupHeader));
    }

    [Fact]
    public void DefaultValues_AreNotRepeatedOnEveryRow()
    {
        // 未排期、无提醒、普通优先级都是默认值，行内不再逐条重复。
        var entry = Assert.Single(TodayViewModel.BuildPlanEntries(
            [Plan(1, null)],
            new Dictionary<int, int>(),
            new Dictionary<int, Anime>(),
            Today));

        Assert.False(entry.HasSubtitle);
        Assert.False(entry.HasPriority);
    }

    [Fact]
    public void ProgressSubtitle_ShowsRealPositionInsteadOfClaimingCompletion()
    {
        var subtitle = TodayViewModel.BuildProgressSubtitle(new AnimeProgressSnapshot(
            1,
            5,
            1438,
            1440,
            new DateTimeOffset(2026, 9, 12, 23, 10, 0, TimeSpan.FromHours(8))));

        // 观看时间放在行尾单独显示，进度说明只保留集数与真实位置。
        Assert.Equal("第 5 集 · 23:58 / 24:00", subtitle);
        Assert.DoesNotContain("看完", subtitle);
    }

    [Fact]
    public void OverduePlan_ShowsDateAndHigherPriorityAsOneWarningLine()
    {
        var entry = Assert.Single(TodayViewModel.BuildPlanEntries(
            [Plan(1, Today.AddDays(-3), AnimePlanPriority.High)],
            new Dictionary<int, int>(),
            new Dictionary<int, Anime>(),
            Today));

        Assert.True(entry.HasOverdueMeta);
        Assert.False(entry.HasNormalMeta);
        Assert.Equal("已逾期 3 天 · 高优先级", entry.Meta);
    }

    [Fact]
    public void RelativeTime_UsesTodayAndYesterdayBeforeFallingBackToDate()
    {
        var watched = new DateTime(2026, 9, 12, 21, 30, 0);

        Assert.Equal("今天 21:30", TodayViewModel.FormatRelativeTime(watched, watched.AddHours(1)));
        Assert.Equal("昨天 21:30", TodayViewModel.FormatRelativeTime(watched, Today.ToDateTime(new TimeOnly(9, 0))));
        Assert.Equal("9月12日 21:30", TodayViewModel.FormatRelativeTime(watched, watched.AddDays(5)));
    }

    [Fact]
    public void RemovingFirstEntryOfGroup_RegroupsHeaderAndCount()
    {
        var entries = TodayViewModel.BuildPlanEntries(
            [Plan(1, null), Plan(2, null), Plan(3, null)],
            new Dictionary<int, int>(),
            new Dictionary<int, Anime>(),
            Today);

        // 屏蔽作品后只过滤条目：组标题挂在首项上，必须重新分组。
        var regrouped = TodayViewModel.GroupPlanEntries(
            entries.Where(entry => entry.Plan.AnimeId != 1),
            Today);

        Assert.Equal(
            new[] { "未排期 · 2", "" },
            regrouped.Select(entry => entry.GroupHeader));
    }

    [Fact]
    public void WithinGroup_TargetDateComesBeforePriority()
    {
        // 服务返回的顺序是优先级优先；今天页写的是“按计划日期排列”。
        var entries = TodayViewModel.BuildPlanEntries(
            [Plan(1, Today.AddDays(5), AnimePlanPriority.High), Plan(2, Today)],
            new Dictionary<int, int>(),
            new Dictionary<int, Anime>(),
            Today);

        Assert.Equal(new[] { 2, 1 }, entries.Select(entry => entry.Plan.AnimeId));
    }

    private static AnimePlan Plan(
        int id,
        DateOnly? target,
        AnimePlanPriority priority = AnimePlanPriority.Normal) => new(
        id,
        $"计划{id}",
        priority,
        target,
        0,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        null,
        null);
}
