using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests
{
    public class SavedTagServiceTests : IAsyncLifetime
    {
        private readonly PersonalDatabaseFixture _database = new();
        private SqliteConnectionFactory DbFactory => _database.Factory;

        public Task InitializeAsync() => _database.InitializeAsync();

        public Task DisposeAsync() => _database.DisposeAsync();

        private SavedTagService CreateService()
        {
            return new SavedTagService(DbFactory);
        }

        [Fact]
        public async Task SaveAndGetAllSavedTags_ReturnsSavedTags()
        {
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
            var svc = CreateService();

            await svc.SaveTagAsync("原创");
            await svc.SaveTagAsync("原创");
            var tags = await svc.GetAllSavedTagsAsync();

            Assert.Single(tags);
        }

        [Fact]
        public async Task RemoveTag_RemovesTag()
        {
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
            var svc = CreateService();

            await svc.SaveTagAsync("原创");

            Assert.True(await svc.IsTagSavedAsync("原创"));
            Assert.False(await svc.IsTagSavedAsync("科幻"));
        }
    }
}
