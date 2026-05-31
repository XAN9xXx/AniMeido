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
        public async Task SaveAndGetSavedTags_ReturnsSavedTags()
        {
            await RunFullMigrationAsync();
            var svc = CreateService();

            await svc.SaveTagAsync(1, "原创");
            await svc.SaveTagAsync(1, "科幻");
            var tags = await svc.GetSavedTagsAsync(1);

            Assert.Equal(2, tags.Count);
            Assert.Contains("原创", tags);
            Assert.Contains("科幻", tags);
        }

        [Fact]
        public async Task SaveTag_Duplicate_DoesNotThrow()
        {
            await RunFullMigrationAsync();
            var svc = CreateService();

            await svc.SaveTagAsync(1, "原创");
            await svc.SaveTagAsync(1, "原创"); // duplicate
            var tags = await svc.GetSavedTagsAsync(1);

            Assert.Single(tags);
        }

        [Fact]
        public async Task RemoveTag_RemovesOnlySpecifiedTag()
        {
            await RunFullMigrationAsync();
            var svc = CreateService();

            await svc.SaveTagAsync(1, "原创");
            await svc.SaveTagAsync(1, "科幻");
            await svc.RemoveTagAsync(1, "原创");

            var tags = await svc.GetSavedTagsAsync(1);
            Assert.Single(tags);
            Assert.Contains("科幻", tags);
        }

        [Fact]
        public async Task GetAnimeIdsByTag_ReturnsCorrectIds()
        {
            await RunFullMigrationAsync();
            var svc = CreateService();

            await svc.SaveTagAsync(1, "原创");
            await svc.SaveTagAsync(2, "原创");
            await svc.SaveTagAsync(3, "科幻");

            var ids = await svc.GetAnimeIdsByTagAsync("原创");
            Assert.Equal(2, ids.Count);
            Assert.Contains(1, ids);
            Assert.Contains(2, ids);
        }

        [Fact]
        public async Task GetAllSavedTags_ReturnsCounts()
        {
            await RunFullMigrationAsync();
            var svc = CreateService();

            await svc.SaveTagAsync(1, "原创");
            await svc.SaveTagAsync(2, "原创");
            await svc.SaveTagAsync(1, "科幻");

            var all = await svc.GetAllSavedTagsAsync();
            Assert.Equal(2, all.Count);

            var original = all.Find(t => t.TagName == "原创");
            Assert.Equal(2, original.Count);

            var sciFi = all.Find(t => t.TagName == "科幻");
            Assert.Equal(1, sciFi.Count);
        }
    }
}
