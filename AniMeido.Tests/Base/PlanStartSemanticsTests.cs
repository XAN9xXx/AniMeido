using AniMeido.Contracts.Models;
using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

/// <summary>
/// “追番中”是追新番，“补番中”是补老番。开始执行补番计划、观察到播放，
/// 都不应把一部老番改归为追番中，也不应把已开始的补番退回未开始。
/// </summary>
public sealed class PlanStartSemanticsTests : DbTestBase
{
    [Fact]
    public async Task StartPlan_KeepsCatchUpStatusArchivesPlanAndCancelsReminders()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        var actionCenter = new ActionCenterService(DbFactory);
        await actionCenter.UpsertPlanAsync(70, "老番", AnimePlanPriority.Normal, null, 0);
        await tracking.SetStatusAsync(70, AnimeTrackingStatus.PlanToWatch);
        var scheduledFor = DateTimeOffset.UtcNow.AddDays(1);
        await actionCenter.AddReminderAsync(new PlanReminder(
            "start-70",
            70,
            PlanReminderKind.Absolute,
            null,
            null,
            scheduledFor,
            scheduledFor,
            PlanReminderState.Pending,
            null,
            null));

        await actionCenter.StartPlanAsync(70);

        Assert.Equal(AnimeTrackingStatus.PlanToWatch, await tracking.GetStatusAsync(70));
        var plan = await actionCenter.GetPlanAsync(70);
        Assert.NotNull(plan?.StartedAt);
        Assert.NotNull(plan?.ArchivedAt);
        Assert.Equal(
            PlanReminderState.Cancelled,
            Assert.Single(await actionCenter.GetRemindersAsync(animeId: 70)).State);
    }

    [Fact]
    public async Task RewritingCatchUpStatus_DoesNotRestartAStartedPlan()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        var actionCenter = new ActionCenterService(DbFactory);
        await actionCenter.UpsertPlanAsync(71, "老番", AnimePlanPriority.Normal, null, 0);
        await tracking.SetStatusAsync(71, AnimeTrackingStatus.PlanToWatch);
        await actionCenter.StartPlanAsync(71);

        // 例如把一部已开始补番的作品再次拖到“补番”区域。
        await tracking.SetStatusAsync(71, AnimeTrackingStatus.PlanToWatch);

        var plan = await actionCenter.GetPlanAsync(71);
        Assert.NotNull(plan?.StartedAt);
        Assert.NotNull(plan?.ArchivedAt);
    }

    [Theory]
    [InlineData(AnimeTrackingStatus.PlanToWatch, AnimeTrackingStatus.PlanToWatch)]
    [InlineData(AnimeTrackingStatus.Following, AnimeTrackingStatus.Watching)]
    [InlineData(AnimeTrackingStatus.Watching, AnimeTrackingStatus.Watching)]
    public async Task RecordingPlayback_PromotesOnlyFollowedAnime(
        AnimeTrackingStatus before,
        AnimeTrackingStatus after)
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        var actionCenter = new ActionCenterService(DbFactory);
        await tracking.SetStatusAsync(72, before);

        await actionCenter.RecordAsync(new AnimePlaybackProgress(
            "playback-72",
            72,
            1,
            120,
            1440,
            false,
            DateTimeOffset.UtcNow));

        Assert.Equal(after, await tracking.GetStatusAsync(72));
    }
}
