using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class BrowseHistoryServiceTests : DbTestBase
{
    [Fact]
    public async Task History_RemainsAvailableWithoutPlaybackData()
    {
        await RunProductionMigrationAsync();
        var service = new BrowseHistoryService(DbFactory);

        await service.RecordAsync(100, "第一部");
        await service.RecordAsync(200, "第二部");

        var history = await service.GetHistoryAsync();

        Assert.Equal(2, history.Count);
        Assert.Equal(200, history[0].AnimeId);
        Assert.All(history, item => Assert.Equal(1, item.ViewCount));
    }

    [Fact]
    public async Task Clear_RemovesAllHistory()
    {
        await RunProductionMigrationAsync();
        var service = new BrowseHistoryService(DbFactory);
        await service.RecordAsync(100, "第一部");

        await service.ClearAsync();

        Assert.Empty(await service.GetHistoryAsync());
    }
}
