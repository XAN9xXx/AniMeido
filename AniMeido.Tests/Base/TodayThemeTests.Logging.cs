using System.Collections.Concurrent;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Extensions.Logging;

namespace AniMeido.Tests;

public sealed partial class TodayThemeTests
{
    private static readonly TimeSpan LoggingBudget = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logging_ThemeFailureErrorsOnlyWhileItStillOwnsTheLoad(bool cancelBeforeFailure)
    {
        await RunProductionMigrationAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var search = new TaskCompletionSource<(List<Anime>, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new HttpRequestException("theme search fixture failure");
        var source = new ThemeSource
        {
            SearchRequest = _ =>
            {
                entered.TrySetResult();
                return search.Task;
            },
        };
        var logger = new ThemeLog();
        var vm = new TodayThemeViewModel(source, new TrackingService(DbFactory),
            _featureState,
            new BrowseHistoryService(DbFactory), new ArchiveService(DbFactory),
            new ActionCenterService(DbFactory), logger);
        var loading = vm.LoadForDateAsync(DateWith(TodayThemeKind.Movies));
        try
        {
            await entered.Task.WaitAsync(LoggingBudget);
            if (cancelBeforeFailure)
                vm.CancelLoad();

            search.SetException(failure);
            await loading.WaitAsync(LoggingBudget);

            Assert.Equal(!cancelBeforeFailure, vm.IsFailed);
            if (cancelBeforeFailure)
            {
                Assert.Empty(logger.Entries);
            }
            else
            {
                var entry = Assert.Single(logger.Entries);
                Assert.Equal(LogLevel.Error, entry.Level);
                Assert.Same(failure, entry.Exception);
            }
        }
        finally
        {
            vm.CancelLoad();
            search.TrySetResult(([], 0));
            await loading.WaitAsync(LoggingBudget);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logging_FailedGenreSampleWarnsOnlyBeforeCancellation(bool cancelBeforeFailure)
    {
        await RunProductionMigrationAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tags = new TaskCompletionSource<List<Tag>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new HttpRequestException("genre sample fixture failure");
        var source = new ThemeSource
        {
            TagsRequest = (id, _) =>
            {
                if (id != 601)
                    return Task.FromResult(new List<Tag> { new("治愈") });

                entered.TrySetResult();
                return tags.Task;
            },
        };
        var logger = new ThemeLog();
        var service = LoggingCreateThemeService(source, logger);
        var context = new TodayThemeContext(Start,
            new Dictionary<int, AnimeTrackingStatus>
            {
                [601] = AnimeTrackingStatus.Completed,
                [602] = AnimeTrackingStatus.Completed,
                [603] = AnimeTrackingStatus.Completed,
            }, ExcludedAnimeId: null);
        using var cancellation = new CancellationTokenSource();
        var building = service.BuildAsync(TodayThemeKind.FavoriteGenre, context, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(LoggingBudget);
            if (cancelBeforeFailure)
                cancellation.Cancel();

            tags.SetException(failure);
            if (cancelBeforeFailure)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => building.WaitAsync(LoggingBudget));
                Assert.Empty(logger.Entries);
            }
            else
            {
                var content = await building.WaitAsync(LoggingBudget);
                Assert.NotNull(content);
                Assert.NotEmpty(content.Candidates);
                Assert.Equal("治愈", source.LastTag);
                var entry = Assert.Single(logger.Entries);
                Assert.Equal(LogLevel.Warning, entry.Level);
                Assert.Same(failure, entry.Exception);
            }
        }
        finally
        {
            cancellation.Cancel();
            tags.TrySetResult([]);
            await LoggingObserveAsync(building).WaitAsync(LoggingBudget);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logging_DetailPlaceholderWarnsOnlyBeforeCancellation(bool cancelBeforeFailure)
    {
        await RunProductionMigrationAsync();
        var browse = new BrowseHistoryService(DbFactory);
        foreach (var id in new[] { 301, 302 })
        {
            await browse.RecordAsync(id, $"浏览记录{id}");
            await browse.RecordAsync(id, $"浏览记录{id}");
        }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detail = new TaskCompletionSource<Anime?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new HttpRequestException("theme detail fixture failure");
        var source = new ThemeSource
        {
            DetailRequest = (id, _) =>
            {
                if (id != 301)
                    return Task.FromResult<Anime?>(Item(id, 7));

                entered.TrySetResult();
                return detail.Task;
            },
        };
        var logger = new ThemeLog();
        var service = LoggingCreateThemeService(source, logger);
        var context = new TodayThemeContext(Start, new Dictionary<int, AnimeTrackingStatus>(), null);
        using var cancellation = new CancellationTokenSource();
        var building = service.BuildAsync(TodayThemeKind.RevisitedBrowse, context, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(LoggingBudget);
            if (cancelBeforeFailure)
                cancellation.Cancel();

            detail.SetException(failure);
            if (cancelBeforeFailure)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => building.WaitAsync(LoggingBudget));
                Assert.Empty(logger.Entries);
            }
            else
            {
                var content = await building.WaitAsync(LoggingBudget);
                Assert.NotNull(content);
                Assert.Equal("浏览记录301", content.Candidates.Single(row => row.Anime.ID == 301).Anime.Title);
                Assert.Equal("作品302", content.Candidates.Single(row => row.Anime.ID == 302).Anime.Title);
                var entry = Assert.Single(logger.Entries);
                Assert.Equal(LogLevel.Warning, entry.Level);
                Assert.Same(failure, entry.Exception);
            }
        }
        finally
        {
            cancellation.Cancel();
            detail.TrySetResult(null);
            await LoggingObserveAsync(building).WaitAsync(LoggingBudget);
        }
    }

    private TodayThemeService LoggingCreateThemeService(ThemeSource source, ILogger logger)
        => new(source, new TrackingService(DbFactory), new BrowseHistoryService(DbFactory),
            new ArchiveService(DbFactory), new ActionCenterService(DbFactory), logger);

    private static async Task LoggingObserveAsync(Task task)
        => await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    private sealed record ThemeLogEntry(LogLevel Level, Exception? Exception);

    private sealed class ThemeLog : ILogger
    {
        public ConcurrentQueue<ThemeLogEntry> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(level))
                Entries.Enqueue(new ThemeLogEntry(level, exception));
        }
    }
}
