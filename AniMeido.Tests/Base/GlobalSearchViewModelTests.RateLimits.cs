using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed partial class GlobalSearchViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RateLimitedSearchShowsTheRetryNoticeWithoutRepeatingTheNetworkLog(bool nextPage)
    {
        await RunProductionMigrationAsync();
        var source = new SearchSource
        {
            Search = (_, offset, _) => nextPage && offset == 0
                ? Task.FromResult((new List<Anime> { Item(1) }, 2))
                : Task.FromException<(List<Anime>, int)>(MetadataServiceException.RateLimited()),
        };
        var logger = new SearchLog();
        var vm = NewViewModel(source, new TrackingService(DbFactory), logger);
        try
        {
            await vm.SearchAsync("synthetic rate limit");
            if (nextPage) await vm.LoadMoreAsync();
            Assert.True(vm.IsError);
            Assert.Equal("请求较多，请稍后重试。", vm.ErrorMessage);
            Assert.Empty(logger.Entries);
        }
        finally { vm.CancelPendingSearch(); }
    }
}
