using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests
{
    public class SavedTagServiceTests : DbTestBase
    {
        private SavedTagService CreateService()
        {
            return new SavedTagService(DbPath);
        }

        [Fact]
        public async Task SaveAndGetAllSavedTags_ReturnsSavedTags()
        {
            await RunFullMigrationAsync();
            var svc = CreateService();

            await svc.SaveTagAsync("原创");
            await svc.SaveTagAsync("科幻");
            var tags = await svc.GetAllSavedTagsAsync();

            Assert.Equal(2, tags.Count);
            Assert.Contains("原创", tags);
            Assert.Contains("科幻", tags);
        }

        [Fact]
        public async Task SaveTag_Duplicate_DoesNotThrow()
        {
            await RunFullMigrationAsync();
            var svc = CreateService();

            await svc.SaveTagAsync("原创");
            await svc.SaveTagAsync("原创"); // duplicate
            var tags = await svc.GetAllSavedTagsAsync();

            Assert.Single(tags);
        }

        [Fact]
        public async Task RemoveTag_RemovesTag()
        {
            await RunFullMigrationAsync();
            var svc = CreateService();

            await svc.SaveTagAsync("原创");
            await svc.SaveTagAsync("科幻");
            await svc.RemoveTagAsync("原创");

            var tags = await svc.GetAllSavedTagsAsync();
            Assert.Single(tags);
            Assert.Contains("科幻", tags);
        }

        [Fact]
        public async Task IsTagSaved_ReturnsCorrectStatus()
        {
            await RunFullMigrationAsync();
            var svc = CreateService();

            await svc.SaveTagAsync("原创");

            Assert.True(await svc.IsTagSavedAsync("原创"));
            Assert.False(await svc.IsTagSavedAsync("科幻"));
        }
    }
}
