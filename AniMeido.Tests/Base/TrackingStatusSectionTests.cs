using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests
{
    public class TrackingStatusSectionTests
    {
        [Fact]
        public void CreateDefaults_MapsEveryManagedStatusOnce()
        {
            var sections = TrackingStatusSection.CreateDefaults();

            Assert.Equal(7, sections.Count);
            Assert.Equal(
                Enum.GetValues<AnimeTrackingStatus>()
                    .Where(status => status != AnimeTrackingStatus.None)
                    .Order(),
                sections.Select(section => section.Status).Order());
        }

        [Fact]
        public void Blocked_RemainsDedicatedSection()
        {
            var section = Assert.Single(
                TrackingStatusSection.CreateDefaults(),
                item => item.Status == AnimeTrackingStatus.Blocked);

            Assert.Equal("屏蔽", section.Label);
            Assert.Contains("屏蔽", section.EmptyMessage);
        }

        [Fact]
        public void Count_UpdatesHeaderAndEmptyState()
        {
            var section = TrackingStatusSection.CreateDefaults()[0];

            section.Count = 2;

            Assert.True(section.HasItems);
            Assert.Equal("追番中 (2)", section.Header);
        }
    }
}
