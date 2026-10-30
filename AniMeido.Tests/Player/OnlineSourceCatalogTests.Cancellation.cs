using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Player.Sources;
using AniMeido.Plugin.Player.Sources.Web;
using Microsoft.Extensions.Logging;
using System.Net.Http;

namespace AniMeido.Tests;

public sealed partial class OnlineSourceCatalogTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("io")]
    [InlineData("http")]
    public async Task CancelledLookupDrainsSourcesWithoutReplacingTheNextLookupOrLoggingLateFailures(string outcome)
    {
        using var cancellation = new CancellationTokenSource();
        var lateSource = new LateCatalogSource("late", outcome switch
        {
            "io" => new IOException("Late source read failure"),
            "http" => new HttpRequestException("Late source request failure"),
            _ => null,
        });
        var otherSource = new LateCatalogSource("other");
        IReadOnlyList<IOnlineAnimeSource> sources = [lateSource, otherSource];
        var logger = new CatalogLogger();
        var catalog = new OnlineSourceCatalog(() => sources, logger: logger);
        var loading = catalog.GetEpisodesAsync(new AnimePlaybackContext(42, "Test Anime"), cancellation.Token);
        try
        {
            await Task.WhenAll(lateSource.Entered.Task, otherSource.Entered.Task).WaitAsync(Budget);
            cancellation.Cancel();
            var currentFailure = new InvalidOperationException("Current source failure");
            sources = [new ErrorSource(currentFailure), new AmbiguousSource()];
            await catalog.ReloadAsync(default).WaitAsync(Budget);
            Assert.Empty(await catalog.GetEpisodesAsync(new AnimePlaybackContext(84, "Test Anime"), default).WaitAsync(Budget));
            var currentDiagnostics = catalog.LastDiagnostics;
            var currentMappings = catalog.LastMappingRequests;
            Assert.Equal("failing", Assert.Single(currentDiagnostics).SourceId);
            Assert.Equal("ambiguous", Assert.Single(currentMappings).SourceId);
            Assert.Same(currentFailure, Assert.Single(logger.Entries).Exception);

            lateSource.Release.TrySetResult();
            await lateSource.Returning.Task.WaitAsync(Budget);
            Assert.False(loading.IsCompleted);
            otherSource.Release.TrySetResult();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading.WaitAsync(Budget));
            Assert.Same(currentDiagnostics, catalog.LastDiagnostics);
            Assert.Same(currentMappings, catalog.LastMappingRequests);
            var entry = Assert.Single(logger.Entries);
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Same(currentFailure, entry.Exception);
        }
        finally
        {
            cancellation.Cancel();
            lateSource.Release.TrySetResult();
            otherSource.Release.TrySetResult();
            await DrainCatalogOperationAsync(loading);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledResolveDoesNotAppendLateFailureToTheNextLookup(bool classifiedFailure)
    {
        using var cancellation = new CancellationTokenSource();
        Exception failure = classifiedFailure
            ? new SourceResolutionException(SourceResolutionFailureKind.RuleMismatch, "Late classified failure")
            : new IOException("Late resolve read failure");
        var lateSource = new LateCatalogSource("late", failure);
        IReadOnlyList<IOnlineAnimeSource> sources = [lateSource];
        var logger = new CatalogLogger();
        var catalog = new OnlineSourceCatalog(() => sources, logger: logger);
        var resolving = catalog.ResolveAsync(new SourceEpisode("late", "episode-1", "Episode 1"), cancellation.Token);
        try
        {
            await lateSource.Entered.Task.WaitAsync(Budget);
            cancellation.Cancel();
            var currentFailure = new InvalidOperationException("Current source failure");
            sources = [new ErrorSource(currentFailure)];
            await catalog.ReloadAsync(default).WaitAsync(Budget);
            Assert.Empty(await catalog.GetEpisodesAsync(new AnimePlaybackContext(84, "Test Anime"), default).WaitAsync(Budget));
            var currentDiagnostics = catalog.LastDiagnostics;
            Assert.Equal("failing", Assert.Single(currentDiagnostics).SourceId);
            lateSource.Release.TrySetResult();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolving.WaitAsync(Budget));
            Assert.Same(currentDiagnostics, catalog.LastDiagnostics);
            var entry = Assert.Single(logger.Entries);
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Same(currentFailure, entry.Exception);
        }
        finally
        {
            cancellation.Cancel();
            lateSource.Release.TrySetResult();
            await DrainCatalogOperationAsync(resolving);
        }
    }

    private static async Task DrainCatalogOperationAsync(Task operation)
    {
        try
        {
            await operation.WaitAsync(Budget);
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or InvalidOperationException or SourceResolutionException)
        {
        }
    }

    private sealed class LateCatalogSource(string id, Exception? failure = null) : IOnlineAnimeSource
    {
        public string Id => id;
        public string DisplayName => id;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Returning { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<SourceEpisode>> GetEpisodesAsync(
            AnimePlaybackContext anime, CancellationToken cancellationToken)
        {
            await CompleteAsync();
            return [new SourceEpisode(Id, "episode-1", "Episode 1")];
        }

        public async Task<ResolvedMedia> ResolveAsync(SourceEpisode episode, CancellationToken cancellationToken)
        {
            await CompleteAsync();
            return new ResolvedMedia(new Uri("https://example.test/episode-1.m3u8"), episode.Title,
                new Dictionary<string, string>());
        }

        private async Task CompleteAsync()
        {
            Entered.TrySetResult();
            // 模拟来源已开始的工作：调用取消后，仍等待真实结果再返回或失败。
            await Release.Task;
            Returning.TrySetResult();
            if (failure is not null)
                throw failure;
        }
    }
}
