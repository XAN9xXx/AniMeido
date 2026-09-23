using AniMeido.Contracts.Models;
using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

/// <summary>
/// 观察到播放时，补番状态和计划不应被隐式改写。
/// </summary>
public sealed class PlanPlaybackSemanticsTests : DbTestBase
{
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
        if (before == AnimeTrackingStatus.PlanToWatch)
        {
            await actionCenter.UpsertPlanAsync(
                72, "老番", AnimePlanPriority.Normal, null, 0);
        }

        await actionCenter.RecordAsync(new AnimePlaybackProgress(
            "playback-72",
            72,
            1,
            120,
            1440,
            false,
            DateTimeOffset.UtcNow));

        Assert.Equal(after, await tracking.GetStatusAsync(72));
        if (before == AnimeTrackingStatus.PlanToWatch)
        {
            var plan = await actionCenter.GetPlanAsync(72);
            Assert.NotNull(plan);
            Assert.Null(plan.StartedAt);
            Assert.Null(plan.ArchivedAt);
        }
    }
}
