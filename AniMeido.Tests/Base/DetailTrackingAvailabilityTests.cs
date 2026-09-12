using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

public sealed class DetailTrackingAvailabilityTests : DbTestBase
{
    [Theory]
    [InlineData(nameof(AnimeReleasePhase.CurrentSeason), true, false)]
    // 未上映的作品也必须能标记追番：Watching 的语义覆盖“未上映，计划追”。
    [InlineData(nameof(AnimeReleasePhase.Upcoming), true, false)]
    [InlineData(nameof(AnimeReleasePhase.Past), false, true)]
    [InlineData(nameof(AnimeReleasePhase.Unknown), false, false)]
    public void ReleasePhase_DecidesWhichSeasonalActionsAreOffered(
        string phase,
        bool expectWatching,
        bool expectPlanToWatch)
    {
        // 可见性只在阶段标志变化时重算；这条路径不会触碰数据源。
        var viewModel = new AnimeDetailViewModel(null!, new TrackingService(DbFactory));

        viewModel.IsCurrentSeason = phase == nameof(AnimeReleasePhase.CurrentSeason);
        viewModel.IsOldSeason = phase == nameof(AnimeReleasePhase.Past);
        viewModel.IsUpcoming = phase == nameof(AnimeReleasePhase.Upcoming);

        Assert.Equal(expectWatching, IsOffered(AnimeTrackingStatus.Watching));
        Assert.Equal(expectPlanToWatch, IsOffered(AnimeTrackingStatus.PlanToWatch));
        // 与季度无关的操作在任何阶段都保持可用。
        Assert.True(IsOffered(AnimeTrackingStatus.Following));

        bool IsOffered(AnimeTrackingStatus status) => viewModel.VisibleTrackingActions
            .Any(action => action.Status == status);
    }
}
