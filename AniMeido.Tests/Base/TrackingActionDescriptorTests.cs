using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests
{
    public class TrackingActionDescriptorTests
    {
        [Fact]
        public void CreateDefaults_MapsEveryActionableStatusOnce()
        {
            var actions = TrackingActionDescriptor.CreateDefaults();

            Assert.Equal(7, actions.Count);
            Assert.Equal(
                Enum.GetValues<AnimeTrackingStatus>()
                    .Where(status => status != AnimeTrackingStatus.None)
                    .Order(),
                actions.Select(action => action.Status).Order());
        }

        [Theory]
        // 本季在播与未上映都属于“可追”，往季才是“可补”。
        [InlineData(true, false, true, false)]
        [InlineData(false, true, false, true)]
        [InlineData(false, false, false, false)]
        public void SeasonalActions_SplitOngoingFromCatchUp(
            bool allowsOngoing,
            bool allowsCatchUp,
            bool expectWatching,
            bool expectPlan)
        {
            var actions = TrackingActionDescriptor.CreateDefaults();
            var watching = Assert.Single(
                actions,
                action => action.Status == AnimeTrackingStatus.Watching);
            var plan = Assert.Single(
                actions,
                action => action.Status == AnimeTrackingStatus.PlanToWatch);

            watching.UpdateAvailability(allowsOngoing, allowsCatchUp);
            plan.UpdateAvailability(allowsOngoing, allowsCatchUp);

            Assert.Equal(expectWatching, watching.IsVisible);
            Assert.Equal(expectPlan, plan.IsVisible);
        }

        [Fact]
        public void NonSeasonalActions_RemainVisibleWhenSeasonIsUnknown()
        {
            var actions = TrackingActionDescriptor.CreateDefaults();

            foreach (var action in actions)
            {
                action.UpdateAvailability(
                    allowsOngoing: false,
                    allowsCatchUp: false);
            }

            Assert.All(
                actions.Where(action =>
                    !action.OngoingOnly
                    && !action.CatchUpOnly),
                action => Assert.True(action.IsVisible));
        }
    }
}
