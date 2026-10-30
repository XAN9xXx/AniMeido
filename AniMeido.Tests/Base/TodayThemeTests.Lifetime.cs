using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed partial class TodayThemeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Mechanism")]
    public async Task Lifetime_CancellationSourceRemainsUsableUntilItsActualLoadFinishes(bool replace)
    {
        await RunProductionMigrationAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<(List<Anime> Results, int Total)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var oldToken = CancellationToken.None;
        var calls = 0;
        var source = new ThemeSource
        {
            SearchRequest = token =>
            {
                if (Interlocked.Increment(ref calls) > 1)
                    return Task.FromResult((new List<Anime> { Item(701, 8, AnimeMediaFormat.Movie) }, 1));

                oldToken = token;
                entered.TrySetResult();
                return release.Task;
            },
        };
        var vm = NewViewModel(source, new TrackingService(DbFactory));
        var date = DateWith(TodayThemeKind.Movies);
        var loading = vm.LoadForDateAsync(date);
        Task replacement = Task.CompletedTask;
        var budget = TimeSpan.FromSeconds(5);
        try
        {
            await entered.Task.WaitAsync(budget);
            if (replace)
            {
                replacement = vm.LoadForDateAsync(date);
                await replacement.WaitAsync(budget);
                Assert.Equal(701, Assert.Single(vm.Items).Anime.ID);
            }
            else
            {
                vm.CancelLoad();
            }

            Assert.True(oldToken.IsCancellationRequested);
            Assert.False(loading.IsCompleted);
            // 真实来源仍在使用令牌；取消不能提前释放它依赖的等待句柄。
            Assert.True(oldToken.WaitHandle.WaitOne(0));
            Assert.False(vm.IsLoading);
            Assert.False(vm.IsFailed);
        }
        finally
        {
            release.TrySetResult(([Item(101, 7, AnimeMediaFormat.Movie)], 1));
            await Task.WhenAll(loading, replacement).WaitAsync(budget);
            vm.CancelLoad();
        }

        Assert.Throws<ObjectDisposedException>(() => oldToken.WaitHandle);
        Assert.False(vm.IsLoading);
        Assert.False(vm.IsFailed);
        if (replace)
            Assert.Equal(701, Assert.Single(vm.Items).Anime.ID);
        else
            Assert.Empty(vm.Items);
    }
}
