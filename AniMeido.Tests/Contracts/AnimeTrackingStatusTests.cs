using AniMeido.Contracts.Models;

namespace AniMeido.Tests
{
    public class AnimeTrackingStatusTests
    {
        [Fact]
        public void Enum_HasAllEightValues()
        {
            var values = Enum.GetValues<AnimeTrackingStatus>();
            Assert.Equal(8, values.Length);
        }

        [Fact]
        public void None_IsZero()
        {
            Assert.Equal(0, (int)AnimeTrackingStatus.None);
        }

        [Fact]
        public void Watching_HasExpectedValue()
        {
            Assert.Equal(1, (int)AnimeTrackingStatus.Watching);
        }

        [Fact]
        public void PlanToWatch_HasExpectedValue()
        {
            Assert.Equal(2, (int)AnimeTrackingStatus.PlanToWatch);
        }

        [Fact]
        public void NotInterested_HasExpectedValue()
        {
            Assert.Equal(3, (int)AnimeTrackingStatus.NotInterested);
        }

        [Fact]
        public void Following_HasExpectedValue()
        {
            Assert.Equal(4, (int)AnimeTrackingStatus.Following);
        }

        [Fact]
        public void Completed_HasExpectedValue()
        {
            Assert.Equal(5, (int)AnimeTrackingStatus.Completed);
        }

        [Fact]
        public void Dropped_HasExpectedValue()
        {
            Assert.Equal(6, (int)AnimeTrackingStatus.Dropped);
        }

        [Fact]
        public void Blocked_HasExpectedValue()
        {
            Assert.Equal(7, (int)AnimeTrackingStatus.Blocked);
        }
    }
}
