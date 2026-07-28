using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests
{
    public class TrackingServiceTests : DbTestBase
    {
        private TrackingService CreateService()
        {
            return new TrackingService(DbFactory);
        }

        [Fact]
        public async Task SetAndGetStatus_ReturnsCorrectStatus()
        {
            await RunProductionMigrationAsync();
            var svc = CreateService();

            await svc.SetStatusAsync(1, AnimeTrackingStatus.Watching);
            var status = await svc.GetStatusAsync(1);

            Assert.Equal(AnimeTrackingStatus.Watching, status);
        }

        [Fact]
        public async Task GetStatus_NotSet_ReturnsNull()
        {
            await RunProductionMigrationAsync();
            var svc = CreateService();

            var status = await svc.GetStatusAsync(999);
            Assert.Null(status);
        }

        [Fact]
        public async Task SetStatus_OverwritesPreviousStatus()
        {
            await RunProductionMigrationAsync();
            var svc = CreateService();

            await svc.SetStatusAsync(1, AnimeTrackingStatus.Watching);
            await svc.SetStatusAsync(1, AnimeTrackingStatus.Completed);
            var status = await svc.GetStatusAsync(1);

            Assert.Equal(AnimeTrackingStatus.Completed, status);
        }

        [Fact]
        public async Task RemoveStatus_ClearsStatus()
        {
            await RunProductionMigrationAsync();
            var svc = CreateService();

            await svc.SetStatusAsync(1, AnimeTrackingStatus.Watching);
            await svc.RemoveStatusAsync(1);
            var status = await svc.GetStatusAsync(1);

            Assert.Null(status);
        }

        [Fact]
        public async Task GetAnimeIdsByStatus_ReturnsOnlyMatchingIds()
        {
            await RunProductionMigrationAsync();
            var svc = CreateService();

            await svc.SetStatusAsync(1, AnimeTrackingStatus.Watching);
            await svc.SetStatusAsync(2, AnimeTrackingStatus.PlanToWatch);
            await svc.SetStatusAsync(3, AnimeTrackingStatus.Watching);

            var watchingIds = await svc.GetAnimeIdsByStatusAsync(AnimeTrackingStatus.Watching);
            var planIds = await svc.GetAnimeIdsByStatusAsync(AnimeTrackingStatus.PlanToWatch);

            Assert.Equal(2, watchingIds.Count);
            Assert.Contains(1, watchingIds);
            Assert.Contains(3, watchingIds);
            Assert.Single(planIds);
            Assert.Contains(2, planIds);
        }

        [Fact]
        public async Task GetAllTrackingAsync_ReturnsAllEntries()
        {
            await RunProductionMigrationAsync();
            var svc = CreateService();

            await svc.SetStatusAsync(1, AnimeTrackingStatus.Watching);
            await svc.SetStatusAsync(2, AnimeTrackingStatus.Completed);

            var all = await svc.GetAllTrackingAsync();
            Assert.Equal(2, all.Count);
            Assert.Contains(all, t => t.AnimeId == 1 && t.Status == AnimeTrackingStatus.Watching);
            Assert.Contains(all, t => t.AnimeId == 2 && t.Status == AnimeTrackingStatus.Completed);
        }

        [Fact]
        public async Task RemoveStatus_NonExistent_DoesNotThrow()
        {
            await RunProductionMigrationAsync();
            var svc = CreateService();

            await svc.RemoveStatusAsync(999);
            // Should not throw
        }

        [Fact]
        public async Task SetStatus_MultipleTimes_DoesNotCorrupt()
        {
            await RunProductionMigrationAsync();
            var svc = CreateService();

            for (int i = 0; i < 10; i++)
            {
                await svc.SetStatusAsync(1, AnimeTrackingStatus.Watching);
                await svc.SetStatusAsync(1, AnimeTrackingStatus.PlanToWatch);
            }

            var status = await svc.GetStatusAsync(1);
            Assert.Equal(AnimeTrackingStatus.PlanToWatch, status);
        }

        [Fact]
        public async Task AllEightStatuses_AreStoredAndRetrieved()
        {
            await RunProductionMigrationAsync();
            var svc = CreateService();

            var allStatuses = Enum.GetValues<AnimeTrackingStatus>()
                .Where(s => s != AnimeTrackingStatus.None).ToList();

            int id = 1;
            foreach (var status in allStatuses)
            {
                await svc.SetStatusAsync(id++, status);
            }

            id = 1;
            foreach (var status in allStatuses)
            {
                var result = await svc.GetStatusAsync(id++);
                Assert.Equal(status, result);
            }
        }

        [Fact]
        public async Task ChangingAwayFromPlanToWatch_ArchivesPlanAndCancelsReminders()
        {
            await RunProductionMigrationAsync();
            var tracking = CreateService();
            var actionCenter = new ActionCenterService(DbFactory);
            await actionCenter.UpsertPlanAsync(
                42,
                "Test anime",
                AnimePlanPriority.Normal,
                null,
                0);
            await actionCenter.AddReminderAsync(new PlanReminder(
                "reminder-42",
                42,
                PlanReminderKind.Absolute,
                null,
                null,
                DateTimeOffset.Now.AddDays(1),
                DateTimeOffset.Now.AddDays(1),
                PlanReminderState.Pending,
                null,
                null));

            await tracking.SetStatusAsync(
                42,
                AnimeTrackingStatus.Completed);

            var plan = await actionCenter.GetPlanAsync(42);
            var reminders = await actionCenter.GetRemindersAsync(
                animeId: 42);
            Assert.NotNull(plan?.ArchivedAt);
            Assert.Equal(
                PlanReminderState.Cancelled,
                Assert.Single(reminders).State);
        }

        [Fact]
        public async Task SettingPlanToWatch_ReactivatesExistingArchivedPlan()
        {
            await RunProductionMigrationAsync();
            var tracking = CreateService();
            var actionCenter = new ActionCenterService(DbFactory);
            await actionCenter.UpsertPlanAsync(
                43,
                "Test anime",
                AnimePlanPriority.Normal,
                null,
                0);
            await tracking.SetStatusAsync(
                43,
                AnimeTrackingStatus.Completed);

            await tracking.SetStatusAsync(
                43,
                AnimeTrackingStatus.PlanToWatch);

            var plan = await actionCenter.GetPlanAsync(43);
            Assert.Null(plan?.ArchivedAt);
            Assert.Null(plan?.StartedAt);
        }
    }
}
