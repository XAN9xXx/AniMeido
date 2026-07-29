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

        [Fact]
        public void SeasonalActions_PreserveCurrentAndPastRules()
        {
            var actions = TrackingActionDescriptor.CreateDefaults();
            var watching = Assert.Single(
                actions,
                action => action.Status == AnimeTrackingStatus.Watching);
            var plan = Assert.Single(
                actions,
                action => action.Status == AnimeTrackingStatus.PlanToWatch);

            watching.UpdateAvailability(
                isCurrentSeason: true,
                isOldSeason: false);
            plan.UpdateAvailability(
                isCurrentSeason: true,
                isOldSeason: false);

            Assert.True(watching.IsVisible);
            Assert.False(plan.IsVisible);

            watching.UpdateAvailability(
                isCurrentSeason: false,
                isOldSeason: true);
            plan.UpdateAvailability(
                isCurrentSeason: false,
                isOldSeason: true);

            Assert.False(watching.IsVisible);
            Assert.True(plan.IsVisible);
        }
    }
}
