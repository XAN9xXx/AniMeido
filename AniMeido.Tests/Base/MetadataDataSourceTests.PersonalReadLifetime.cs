using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed partial class MetadataDataSourceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersonalDetail_ReleasesLocalReadBeforeWaitingForRemoteResponse(bool failRequest)
    {
        await RunProductionMigrationAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var budget = TimeSpan.FromSeconds(5);
        using var handler = new StubHandler(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(budget, cancellationToken);
            if (failRequest)
            {
                throw new HttpRequestException("Synthetic remote failure.");
            }

            return Raw(DetailJson);
        });
        using var client = Client(handler);
        using var requestCancellation = new CancellationTokenSource();
        var personal = new PersonalAnimeDataSource(Source(client), DbFactory);
        var detail = personal.GetAnimeDetailAsync(42, requestCancellation.Token);
        Exception? primaryFailure = null;

        try
        {
            await started.Task.WaitAsync(budget);
            using (var import = DbFactory.Access.BeginExclusive())
            {
                Assert.True(import.WaitForDrainAsync(default).IsCompletedSuccessfully,
                    "远端响应尚未返回时，本地读取连接应已释放，不应阻塞导入排空。");
                Assert.False(detail.IsCompleted);
            }

            release.TrySetResult();
            if (failRequest)
            {
                await Assert.ThrowsAsync<AniMeido.Plugin.Base.Exceptions.MetadataServiceException>(() => detail.WaitAsync(budget));
            }
            else
            {
                var anime = Assert.IsType<AniMeido.Contracts.Models.Anime>(await detail.WaitAsync(budget));
                Assert.Equal(42, anime.ID);
                Assert.Equal("中文名", anime.Title);
            }
        }
#pragma warning disable CA1031 // 保留主失败；清理也超时时将两者一起报告。
        catch (Exception ex)
        {
            primaryFailure = ex;
            throw;
        }
#pragma warning restore CA1031
        finally
        {
            release.TrySetResult();
            requestCancellation.Cancel();
            await WaitForObservedCompletionAsync(detail, budget, primaryFailure);
        }
    }
}
