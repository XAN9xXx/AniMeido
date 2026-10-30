using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMeido.Tests;

public sealed partial class MetadataDataSourceTests
{
    private static readonly TimeSpan FinalBoundaryBudget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task FinalBoundary_CurrentSearchRecordsOnlyOneErrorAndPreservesTheHttpCause()
    {
        await RunProductionMigrationAsync();
        var failure = new HttpRequestException("synthetic search failure");
        using var handler = new StubHandler(_ => throw failure);
        using var client = Client(handler);
        var log = new MetadataLogCapture();
        var source = new MetadataDataSource(log, client, new CacheService(DbFactory), new TestTime());
        var vm = CreateFinalBoundarySearch(source, log);
        var searching = vm.SearchAsync("合成搜索");
        try
        {
            await searching.WaitAsync(FinalBoundaryBudget);

            Assert.True(vm.IsError);
            Assert.Empty(vm.Results);
            Assert.StartsWith("/v1/search?", Assert.Single(handler.GetPaths));
            var entry = Assert.Single(log.Entries);
            Assert.Equal(LogLevel.Error, entry.Level);
            var error = Assert.IsType<MetadataServiceException>(entry.Error);
            Assert.Same(failure, error.InnerException);
        }
        finally
        {
            vm.CancelPendingSearch();
            await ObserveFinalBoundaryAsync(searching).WaitAsync(FinalBoundaryBudget);
        }
    }

    [Fact]
    public async Task FinalBoundary_ExpiredDetailRecoveryRecordsOnlyOneWarning()
    {
        await CreateBaseTablesAsync();
        var cache = new CacheService(DbFactory);
        await cache.SetCacheAsync("metadata:detail:42", DetailJson, TimeSpan.FromDays(-1));
        var failure = new HttpRequestException("synthetic expired-detail failure");
        using var handler = new StubHandler(_ => throw failure);
        using var client = Client(handler);
        var log = new MetadataLogCapture();
        var source = new MetadataDataSource(log, client, cache, new TestTime());

        var anime = await source.GetAnimeDetailAsync(42, CancellationToken.None).WaitAsync(FinalBoundaryBudget);

        Assert.NotNull(anime);
        Assert.Equal(42, anime.ID);
        Assert.Equal("中文名", anime.Title);
        Assert.Equal("/v1/works/42", Assert.Single(handler.GetPaths));
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("stale metadata cache", entry.Message);
        var error = Assert.IsType<MetadataServiceException>(entry.Error);
        Assert.Same(failure, error.InnerException);
    }

    [Fact]
    public async Task FinalBoundary_HiddenRatingFallbackRecordsOnlyOneWarning()
    {
        await CreateBaseTablesAsync();
        var failure = new HttpRequestException("synthetic rating failure");
        using var handler = new StubHandler(_ => throw failure);
        using var client = Client(handler);
        var log = new MetadataLogCapture();
        var source = new MetadataDataSource(log, client, new CacheService(DbFactory), new TestTime());

        var text = await source.GetRatingTextAsync(42, CancellationToken.None).WaitAsync(FinalBoundaryBudget);

        Assert.Empty(text);
        Assert.Equal("/v1/works/42", Assert.Single(handler.GetPaths));
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("leaving rating block hidden", entry.Message);
        var error = Assert.IsType<MetadataServiceException>(entry.Error);
        Assert.Same(failure, error.InnerException);
    }

    [Fact]
    public async Task FinalBoundary_ActiveSearchCancellationDoesNotRecordAnErrorOrWarning()
    {
        await RunProductionMigrationAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHandler(async (_, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            started.TrySetResult();
            await release.Task;
            token.ThrowIfCancellationRequested();
            return Reply(new { total = 0, items = Array.Empty<object>() });
        });
        using var client = Client(handler);
        var log = new MetadataLogCapture();
        var source = new MetadataDataSource(log, client, new CacheService(DbFactory), new TestTime());
        var vm = CreateFinalBoundarySearch(source, log);
        var searching = vm.SearchAsync("合成取消");
        try
        {
            await started.Task.WaitAsync(FinalBoundaryBudget);
            vm.CancelPendingSearch();
            await cancelled.Task.WaitAsync(FinalBoundaryBudget);
            release.TrySetResult();
            await searching.WaitAsync(FinalBoundaryBudget);

            Assert.False(vm.IsError);
            Assert.Empty(log.Entries);
            Assert.StartsWith("/v1/search?", Assert.Single(handler.GetPaths));
        }
        finally
        {
            vm.CancelPendingSearch();
            release.TrySetResult();
            await ObserveFinalBoundaryAsync(searching).WaitAsync(FinalBoundaryBudget);
        }
    }

    [Fact]
    public async Task FinalBoundary_DirectLowLevelFailurePropagatesWithoutRecordingItsOwnLog()
    {
        await CreateBaseTablesAsync();
        var failure = new HttpRequestException("synthetic direct failure");
        using var handler = new StubHandler(_ => throw failure);
        using var client = Client(handler);
        var log = new MetadataLogCapture();
        var source = new MetadataDataSource(log, client, new CacheService(DbFactory), new TestTime());

        var error = await Assert.ThrowsAsync<MetadataServiceException>(() =>
            source.GetAnimeDetailAsync(42, CancellationToken.None).WaitAsync(FinalBoundaryBudget));

        Assert.Same(failure, error.InnerException);
        Assert.Equal("/v1/works/42", Assert.Single(handler.GetPaths));
        Assert.Empty(log.Entries);
    }

    private GlobalSearchViewModel CreateFinalBoundarySearch(MetadataDataSource source, MetadataLogCapture log)
    {
        var tracking = new TrackingService(DbFactory);
        return new GlobalSearchViewModel(source, tracking,
            FeatureState,
            new LocalSearchService(tracking, source, NullLogger<LocalSearchService>.Instance),
            new SavedTagService(DbFactory), log);
    }

    private static async Task ObserveFinalBoundaryAsync(Task task)
        => await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
}
