using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

public sealed partial class PastSeasonStatusReloadTests
{
    [Fact]
    public async Task RateLimitedSeasonShowsTheRetryNoticeWithoutRepeatingTheNetworkLog()
    {
        var logger = new SeasonLog();
        var vm = new PastSeasonViewModel(new SeasonSource
        {
            Load = (_, _, _) => Task.FromException<List<Anime>>(MetadataServiceException.RateLimited()),
        }, new TrackingService(DbFactory), logger);
        await vm.LoadPastSeasonAnimeAsync(2026, Season.Spring);
        Assert.True(vm.IsError);
        Assert.Equal("请求较多，请稍后重试。", vm.ErrorMessage);
        Assert.Empty(logger.Entries);
    }
}
