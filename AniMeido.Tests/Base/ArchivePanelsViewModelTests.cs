using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

public sealed class ArchivePanelsViewModelTests : DbTestBase
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset RecordedAt = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ProductionQueries_PublishEachCompletePanelAndKeepScreenshotTags()
    {
        await RunProductionMigrationAsync();
        var archive = new ArchiveService(DbFactory);
        await archive.UpsertArchiveAsync(42, "完整档案", 8.5, "原始笔记");
        await archive.SetAnimeTagsAsync(42, ["个人标签"]);
        var recordedAt = new DateTimeOffset(DateTime.Now.Year, 6, 1, 12, 0, 0, TimeSpan.Zero);
        await archive.AddEntryAsync(42, recordedAt, 3, "第三集感想");
        var screenshot = new AnimeScreenshot(
            "shot", Path.Combine(Path.GetDirectoryName(DbPath)!, "missing.png"), "",
            recordedAt, "窗口", "播放器", 1920, 1080, 42, "完整档案", 3, 90, "截图备注", false);
        await archive.InsertScreenshotAsync(screenshot);
        await archive.AddScreenshotTagsAsync(["shot"], ["截图标签"]);
        var vm = new ArchivePanelsViewModel(new ArchiveQueryService(archive));
        var published = new List<ArchivePanelKind>();
        var completed = new List<(ArchivePanelKind Panel, Exception? Error)>();
        vm.PanelPublished += published.Add;
        vm.PanelLoadCompleted += (panel, error) => completed.Add((panel, error));
        var year = DateTime.Now.Year;

        await vm.EnsureActiveAsync(year, CancellationToken.None);
        var item = Assert.Single(vm.Archives);
        Assert.Equal(42, item.Archive.AnimeId);
        Assert.Equal("完整档案", item.Archive.TitleSnapshot);
        Assert.Equal(8.5, item.Archive.PersonalRating);
        Assert.Equal("原始笔记", item.Archive.SummaryNote);
        Assert.Equal(["个人标签"], item.Tags);
        Assert.Equal(1, item.EntryCount);
        Assert.Equal(1, item.ScreenshotCount);

        vm.SelectPanel(ArchivePanelKind.Statistics);
        await vm.EnsureActiveAsync(year, CancellationToken.None);
        var statistics = Assert.IsType<ArchiveStatistics>(vm.Statistics);
        Assert.Equal(1, statistics.ArchiveCount);
        Assert.Equal(1, statistics.RatedCount);
        Assert.Equal(1, statistics.EntryCount);
        Assert.Equal(1, statistics.ScreenshotCount);
        Assert.Equal(1, statistics.TagCounts["个人标签"]);

        vm.SelectPanel(ArchivePanelKind.Review);
        await vm.EnsureActiveAsync(year, CancellationToken.None);
        var review = Assert.IsType<ArchiveReviewData>(vm.Review);
        Assert.Equal(year, review.Year);
        Assert.Equal(42, Assert.Single(review.Covers).AnimeId);
        Assert.Equal("第三集感想", Assert.Single(review.Moments).Entry.Body);
        Assert.Empty(review.Screenshots);

        vm.SelectPanel(ArchivePanelKind.Screenshots);
        await vm.EnsureActiveAsync(year, CancellationToken.None);
        Assert.Equal(screenshot, Assert.Single(vm.Screenshots));
        Assert.Equal(["截图标签"], vm.ScreenshotTags["shot"]);
        vm.ReplaceScreenshotTags("shot", ["新标签"]);
        Assert.Equal(["新标签"], vm.ScreenshotTags["shot"]);
        Assert.Equal(
            new[] { ArchivePanelKind.Archives, ArchivePanelKind.Statistics, ArchivePanelKind.Review, ArchivePanelKind.Screenshots },
            published);
        Assert.Equal(published, completed.Select(item => item.Panel));
        Assert.All(completed, item => Assert.Null(item.Error));
    }

    [Fact]
    public async Task SwitchingPanels_CancelsOnlyInflightWorkAndReusesPublishedPanel()
    {
        var oldArchives = new TaskCompletionSource<IReadOnlyList<ArchiveListItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var statistics = Statistics(7);
        var archiveCalls = 0;
        var statisticsCalls = 0;
        var oldToken = CancellationToken.None;
        var vm = new ArchivePanelsViewModel(
            token =>
            {
                archiveCalls++;
                oldToken = token;
                return oldArchives.Task;
            },
            _ =>
            {
                statisticsCalls++;
                return Task.FromResult(statistics);
            },
            (year, _) => Task.FromResult(Review(year)),
            _ => Task.FromResult(EmptyScreenshots()));
        var published = new List<ArchivePanelKind>();
        vm.PanelPublished += published.Add;
        using var lifetime = new CancellationTokenSource();
        var oldLoad = vm.EnsureActiveAsync(2026, lifetime.Token);

        try
        {
            Assert.Equal(1, archiveCalls);
            vm.SelectPanel(ArchivePanelKind.Statistics);
            Assert.True(oldToken.IsCancellationRequested);
            await vm.EnsureActiveAsync(2026, lifetime.Token);
            Assert.Same(statistics, vm.Statistics);

            oldArchives.TrySetResult([Archive(99)]);
            await oldLoad.WaitAsync(Budget);
            Assert.Empty(vm.Archives);
            Assert.Equal([ArchivePanelKind.Statistics], published);

            vm.SelectPanel(ArchivePanelKind.Screenshots);
            vm.SelectPanel(ArchivePanelKind.Statistics);
            await vm.EnsureActiveAsync(2026, lifetime.Token);
            Assert.Equal(1, statisticsCalls);
            Assert.Same(statistics, vm.Statistics);
            Assert.Equal([ArchivePanelKind.Statistics], published);
        }
        finally
        {
            oldArchives.TrySetResult([]);
            lifetime.Cancel();
            vm.CancelInflight();
            await oldLoad.WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task ReviewInvalidation_UsesNewYearAndRejectsLateOldYearWithoutLoadingInactivePanels()
    {
        var oldReview = new TaskCompletionSource<ArchiveReviewData>(TaskCreationOptions.RunContinuationsAsynchronously);
        var years = new List<int>();
        var statisticsCalls = 0;
        var oldToken = CancellationToken.None;
        var currentReview = Review(2026);
        var vm = new ArchivePanelsViewModel(
            _ => Task.FromResult<IReadOnlyList<ArchiveListItem>>([]),
            _ =>
            {
                statisticsCalls++;
                return Task.FromResult(Statistics(0));
            },
            (year, token) =>
            {
                years.Add(year);
                if (year != 2025)
                    return Task.FromResult(currentReview);
                oldToken = token;
                return oldReview.Task;
            },
            _ => Task.FromResult(EmptyScreenshots()));
        var published = new List<ArchivePanelKind>();
        vm.PanelPublished += published.Add;
        using var lifetime = new CancellationTokenSource();
        vm.SelectPanel(ArchivePanelKind.Review);
        var oldLoad = vm.EnsureActiveAsync(2025, lifetime.Token);

        try
        {
            vm.Invalidate(ArchivePanelKind.Review, ArchivePanelKind.Statistics);
            Assert.True(oldToken.IsCancellationRequested);
            Assert.Equal(0, statisticsCalls);
            await vm.EnsureActiveAsync(2026, lifetime.Token);
            Assert.Same(currentReview, vm.Review);

            oldReview.TrySetResult(Review(2025));
            await oldLoad.WaitAsync(Budget);
            Assert.Same(currentReview, vm.Review);
            Assert.Equal(new[] { 2025, 2026 }, years);
            Assert.Equal([ArchivePanelKind.Review], published);
            Assert.Equal(0, statisticsCalls);
        }
        finally
        {
            oldReview.TrySetResult(Review(2025));
            lifetime.Cancel();
            vm.CancelInflight();
            await oldLoad.WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task Returning_ReusesCompletedPanelsRetriesInterruptedWorkAndReportsOnlyCurrentFailure()
    {
        var oldArchives = new TaskCompletionSource<IReadOnlyList<ArchiveListItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentError = new IOException("当前请求失败");
        var unknownError = new FormatException("未知错误仍交给调用者");
        var archiveCalls = 0;
        var statisticsCalls = 0;
        var oldToken = CancellationToken.None;
        var vm = new ArchivePanelsViewModel(
            token =>
            {
                switch (++archiveCalls)
                {
                    case 1:
                        oldToken = token;
                        return oldArchives.Task;
                    case 2:
                        return Task.FromException<IReadOnlyList<ArchiveListItem>>(currentError);
                    case 3:
                        return Task.FromResult<IReadOnlyList<ArchiveListItem>>([Archive(2)]);
                    default:
                        return Task.FromException<IReadOnlyList<ArchiveListItem>>(unknownError);
                }
            },
            _ =>
            {
                statisticsCalls++;
                return Task.FromResult(Statistics(1));
            },
            (year, _) => Task.FromResult(Review(year)),
            _ => Task.FromResult(EmptyScreenshots()));
        var completed = new List<(ArchivePanelKind Panel, Exception? Error)>();
        vm.PanelLoadCompleted += (panel, error) => completed.Add((panel, error));
        using var oldLifetime = new CancellationTokenSource();
        using var newLifetime = new CancellationTokenSource();
        vm.SelectPanel(ArchivePanelKind.Statistics);
        await vm.EnsureActiveAsync(2026, oldLifetime.Token);
        vm.SelectPanel(ArchivePanelKind.Archives);
        var oldLoad = vm.EnsureActiveAsync(2026, oldLifetime.Token);

        try
        {
            oldLifetime.Cancel();
            vm.CancelInflight();
            Assert.True(oldToken.IsCancellationRequested);
            vm.SelectPanel(ArchivePanelKind.Statistics);
            await vm.EnsureActiveAsync(2026, newLifetime.Token);
            Assert.Equal(1, statisticsCalls);

            vm.SelectPanel(ArchivePanelKind.Archives);
            await vm.EnsureActiveAsync(2026, newLifetime.Token);
            Assert.Same(currentError, completed[^1].Error);
            await vm.EnsureActiveAsync(2026, newLifetime.Token);
            Assert.Equal(2, Assert.Single(vm.Archives).Archive.AnimeId);
            Assert.Null(completed[^1].Error);
            var completionCount = completed.Count;

            oldArchives.TrySetException(new IOException("离页前的旧失败"));
            await oldLoad.WaitAsync(Budget);
            Assert.Equal(completionCount, completed.Count);
            Assert.Single(completed, item => item.Error is not null);
            Assert.Equal(2, Assert.Single(vm.Archives).Archive.AnimeId);

            vm.Invalidate(ArchivePanelKind.Archives);
            var error = await Assert.ThrowsAsync<FormatException>(() => vm.EnsureActiveAsync(2026, newLifetime.Token));
            Assert.Same(unknownError, error);
            Assert.Equal(completionCount, completed.Count);
        }
        finally
        {
            oldArchives.TrySetResult([]);
            oldLifetime.Cancel();
            newLifetime.Cancel();
            vm.CancelInflight();
            await oldLoad.WaitAsync(Budget);
        }
    }

    private static ArchiveListItem Archive(int id)
        => new(new AnimeArchive(id, $"作品{id}", 8, "笔记", RecordedAt, RecordedAt), ["标签"], 1, 1, AnimeTrackingStatus.Completed);

    private static ArchiveStatistics Statistics(int count)
        => new(RecordedAt, count, count, count, count, 0, 0, 0, new Dictionary<string, int> { ["标签"] = count });

    private static ArchiveReviewData Review(int year)
        => new(year, Statistics(1), [new ArchiveReviewCover(year, $"年度{year}", RecordedAt)], [], []);

    private static ArchiveScreenshotData EmptyScreenshots()
        => new([], new Dictionary<string, IReadOnlyList<string>>());
}
