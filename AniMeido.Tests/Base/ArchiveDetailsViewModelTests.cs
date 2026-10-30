using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Data.Sqlite;

namespace AniMeido.Tests;

public sealed class ArchiveDetailsViewModelTests : DbTestBase
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset RecordedAt = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ProductionQueriesPublishTheCompleteTimelineAndOnlyExistingDetailScreenshots()
    {
        await RunProductionMigrationAsync();
        var archive = new ArchiveService(DbFactory);
        await archive.UpsertArchiveAsync(42, "完整档案", 8.5, "完整笔记");
        await archive.SetAnimeTagsAsync(42, ["个人标签"]);
        await archive.AddEntryAsync(42, RecordedAt, 3, "第三集感想");
        await archive.AddManualWatchEventAsync(new ManualWatchEvent(
            "manual", 42, "完整档案", RecordedAt.AddDays(1), 1, 3, 72, "完整观看备注", RecordedAt));
        for (var index = 0; index < 4; index++)
        {
            var path = Path.Combine(Path.GetDirectoryName(DbPath)!, $"shot-{index}.png");
            if (index < 3)
                await File.WriteAllTextAsync(path, "synthetic image contents");
            await archive.InsertScreenshotAsync(Screenshot($"shot-{index}", 42, index < 3) with { FilePath = path });
        }
        var selected = Assert.Single(await archive.GetArchiveListAsync());
        var model = new ArchiveDetailsViewModel(new ArchiveQueryService(archive), () => true);
        var publications = new List<string>();
        model.SelectionChanged += (_, _) => publications.Add("selection");
        model.TimelinePublished += () => publications.Add("timeline");
        model.DetailsPublished += () => publications.Add("details");

        try
        {
            await model.SelectAsync(selected).WaitAsync(Budget);

            Assert.Same(selected, model.SelectedArchive);
            Assert.Equal("完整笔记", model.SelectedArchive?.Archive.SummaryNote);
            Assert.Equal(["个人标签"], model.SelectedArchive?.Tags);
            Assert.Equal(["观看记录", "感想"], model.TimelineItems.Select(item => item.Kind));
            Assert.Equal("第三集感想", model.TimelineItems[1].Body);
            Assert.Equal(3, model.DetailScreenshots.Count);
            Assert.All(model.DetailScreenshots, item => Assert.True(item.FileExists));
            Assert.Equal(["selection", "timeline", "details"], publications);
        }
        finally
        {
            await model.CancelInflightAsync().WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task NewSelectionClearsPreviousEntriesBeforePublishingItsIdentityWhileCoverWorkCanFinishLater()
    {
        var pendingDetail = NewCompletion<ArchiveDetailData>();
        var pendingCover = NewCompletion();
        var coverStarted = false;
        var coverPublished = false;
        var selectionVersion = 0;
        var first = Detail(1);
        var next = Detail(2);
        var model = CreateModel((id, _, _) => id == 1 ? Task.FromResult(first) : pendingDetail.Task);
        Task selecting = Task.CompletedTask;
        Task cover = Task.CompletedTask;
        model.SelectionChanged += (item, version) =>
        {
            if (item?.Archive.AnimeId != 2)
                return;
            Assert.Empty(model.TimelineItems);
            selectionVersion = version;
            cover = CompleteCoverAsync(version);
        };

        async Task CompleteCoverAsync(int version)
        {
            coverStarted = true;
            await pendingCover.Task;
            if (model.IsCurrentSelection(version))
                coverPublished = true;
        }

        try
        {
            await model.SelectAsync(Archive(1)).WaitAsync(Budget);
            selecting = model.SelectAsync(Archive(2));

            Assert.True(coverStarted);
            Assert.False(selecting.IsCompleted);
            Assert.Empty(model.TimelineItems);
            Assert.Empty(model.DetailScreenshots);
            Assert.Equal(2, model.SelectedArchive?.Archive.AnimeId);
            Assert.True(model.IsCurrentSelection(selectionVersion));

            pendingDetail.TrySetResult(next);
            await selecting.WaitAsync(Budget);
            Assert.Equal("感想2", Assert.Single(model.TimelineItems).Body);
            Assert.False(cover.IsCompleted);

            await model.SelectAsync(null).WaitAsync(Budget);
            Assert.False(model.IsCurrentSelection(selectionVersion));
            pendingCover.TrySetResult();
            await cover.WaitAsync(Budget);
            Assert.False(coverPublished);
            Assert.Null(model.SelectedArchive);
            Assert.Empty(model.TimelineItems);
            Assert.Empty(model.DetailScreenshots);
            Assert.False(model.HasActiveSelection);
        }
        finally
        {
            pendingDetail.TrySetResult(next);
            pendingCover.TrySetResult();
            await ObserveAsync(selecting, cover).WaitAsync(Budget);
            await model.CancelInflightAsync().WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task FailedReplacementKeepsTheNewIdentityWithoutExposingPreviousEntries()
    {
        var pending = NewCompletion<ArchiveDetailData>();
        var failure = new IOException("替换详情读取失败");
        var failures = new List<Exception>();
        var model = CreateModel((id, _, _) => id == 1 ? Task.FromResult(Detail(1)) : pending.Task);
        model.LoadFailed += failures.Add;
        Task selecting = Task.CompletedTask;

        try
        {
            await model.SelectAsync(Archive(1)).WaitAsync(Budget);
            Assert.Single(model.TimelineItems);
            selecting = model.SelectAsync(Archive(2));
            Assert.Equal(2, model.SelectedArchive?.Archive.AnimeId);
            Assert.Empty(model.TimelineItems);

            pending.TrySetException(failure);
            await selecting.WaitAsync(Budget);
            Assert.Same(failure, Assert.Single(failures));
            Assert.Equal(2, model.SelectedArchive?.Archive.AnimeId);
            Assert.Empty(model.TimelineItems);
        }
        finally
        {
            pending.TrySetResult(Detail(2));
            await ObserveAsync(selecting).WaitAsync(Budget);
            await model.CancelInflightAsync().WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task RapidSelectionsExposeOnlyTheFinalArchivesEntriesEvenWhenEarlierWorkFinishesLast()
    {
        var second = NewCompletion<ArchiveDetailData>();
        var third = NewCompletion<ArchiveDetailData>();
        var model = CreateModel((id, _, _) => id switch
        {
            2 => second.Task,
            3 => third.Task,
            _ => Task.FromResult(Detail(id)),
        });
        Task selectingSecond = Task.CompletedTask;
        Task selectingThird = Task.CompletedTask;

        try
        {
            await model.SelectAsync(Archive(1)).WaitAsync(Budget);
            selectingSecond = model.SelectAsync(Archive(2));
            selectingThird = model.SelectAsync(Archive(3));
            Assert.Equal(3, model.SelectedArchive?.Archive.AnimeId);
            Assert.Empty(model.TimelineItems);

            third.TrySetResult(Detail(3));
            await selectingThird.WaitAsync(Budget);
            Assert.All(model.TimelineItems, item => Assert.Equal(3, item.Entry?.AnimeId));
            second.TrySetResult(Detail(2));
            await selectingSecond.WaitAsync(Budget);
            Assert.Equal("感想3", Assert.Single(model.TimelineItems).Body);
        }
        finally
        {
            second.TrySetResult(Detail(2));
            third.TrySetResult(Detail(3));
            await ObserveAsync(selectingSecond, selectingThird).WaitAsync(Budget);
            await model.CancelInflightAsync().WaitAsync(Budget);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldDetailResultOrFailureCannotReplaceTheNewSelection(bool failOldRequest)
    {
        var pending = NewCompletion<ArchiveDetailData>();
        var oldToken = CancellationToken.None;
        var failures = new List<Exception>();
        var model = CreateModel((id, _, token) =>
        {
            if (id != 1)
                return Task.FromResult(Detail(id));
            oldToken = token;
            return pending.Task;
        });
        model.LoadFailed += failures.Add;
        var oldLoad = model.SelectAsync(Archive(1));

        try
        {
            await model.SelectAsync(Archive(2)).WaitAsync(Budget);
            await WaitForCancellationAsync(oldToken);
            if (failOldRequest)
                pending.TrySetException(new IOException("旧详情失败"));
            else
                pending.TrySetResult(Detail(1));
            await oldLoad.WaitAsync(Budget);

            Assert.Equal(2, model.SelectedArchive?.Archive.AnimeId);
            Assert.Equal("感想2", Assert.Single(model.TimelineItems).Body);
            Assert.Equal("shot-2", Assert.Single(model.DetailScreenshots).ScreenshotId);
            Assert.Empty(failures);
        }
        finally
        {
            pending.TrySetResult(Detail(1));
            await ObserveAsync(oldLoad).WaitAsync(Budget);
            await model.CancelInflightAsync().WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task LeavingInvalidatesImmediatelyAndDrainsOldWorkWithoutHoldingUpAReturnedSelection()
    {
        var pending = NewCompletion<ArchiveDetailData>();
        var calls = 0;
        var version = 0;
        var oldToken = CancellationToken.None;
        var failures = new List<Exception>();
        var model = CreateModel((_, _, token) =>
        {
            if (++calls != 1)
                return Task.FromResult(Detail(1, "返回后的感想"));
            oldToken = token;
            return pending.Task;
        });
        model.SelectionChanged += (_, current) => version = current;
        model.LoadFailed += failures.Add;
        var selected = Archive(1);
        var oldLoad = model.SelectAsync(selected);
        var oldVersion = version;
        Task stopping = Task.CompletedTask;
        Task returned = Task.CompletedTask;

        try
        {
            stopping = model.CancelInflightAsync();
            Assert.False(stopping.IsCompleted);
            await WaitForCancellationAsync(oldToken);
            Assert.False(model.IsCurrentSelection(oldVersion));
            Assert.False(model.HasActiveSelection);
            Assert.Same(selected, model.SelectedArchive);

            returned = model.SelectAsync(selected);
            await returned.WaitAsync(Budget);
            Assert.True(model.HasActiveSelection);
            Assert.True(model.IsCurrentSelection(version));
            Assert.Equal("返回后的感想", Assert.Single(model.TimelineItems).Body);
            Assert.False(stopping.IsCompleted);

            pending.TrySetException(new IOException("离页前的旧失败"));
            await Task.WhenAll(oldLoad, stopping).WaitAsync(Budget);
            Assert.Equal("返回后的感想", Assert.Single(model.TimelineItems).Body);
            Assert.True(model.HasActiveSelection);
            Assert.Empty(failures);
        }
        finally
        {
            pending.TrySetResult(Detail(1));
            await ObserveAsync(oldLoad, stopping, returned).WaitAsync(Budget);
            await model.CancelInflightAsync().WaitAsync(Budget);
        }
    }

    [Theory]
    [InlineData("io")]
    [InlineData("state")]
    [InlineData("sqlite")]
    public async Task CurrentKnownDetailFailureIsReportedOnceAndTheSelectionCanBeRetried(string kind)
    {
        var error = KnownFailure(kind);
        var calls = 0;
        var failures = new List<Exception>();
        var model = CreateModel((_, _, _) => ++calls == 1
            ? Task.FromException<ArchiveDetailData>(error)
            : Task.FromResult(Detail(1)));
        model.LoadFailed += failures.Add;
        var selected = Archive(1);

        try
        {
            await model.SelectAsync(selected).WaitAsync(Budget);
            Assert.Same(error, Assert.Single(failures));
            Assert.Same(selected, model.SelectedArchive);
            Assert.Empty(model.TimelineItems);
            Assert.Empty(model.DetailScreenshots);

            await model.SelectAsync(selected).WaitAsync(Budget);
            Assert.Single(failures);
            Assert.Equal("感想1", Assert.Single(model.TimelineItems).Body);
        }
        finally
        {
            await model.CancelInflightAsync().WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task UnknownDetailFailureAndTimelineRefreshFailureStillReachTheirOriginalWaiter()
    {
        var unknown = new FormatException("未处理的详情格式错误");
        var refreshFailure = new IOException("时间线读取失败");
        var calls = 0;
        var failures = new List<Exception>();
        var model = CreateModel(
            (_, _, _) => ++calls == 1
                ? Task.FromException<ArchiveDetailData>(unknown)
                : Task.FromResult(Detail(1)),
            (_, _, _) => Task.FromException<ArchiveTimelineData>(refreshFailure));
        model.LoadFailed += failures.Add;

        try
        {
            var detailError = await Assert.ThrowsAsync<FormatException>(
                () => model.SelectAsync(Archive(1)).WaitAsync(Budget));
            Assert.Same(unknown, detailError);
            await model.SelectAsync(Archive(1)).WaitAsync(Budget);
            var timelineError = await Assert.ThrowsAsync<IOException>(
                () => model.RefreshTimelineAsync(1).WaitAsync(Budget));
            Assert.Same(refreshFailure, timelineError);
            Assert.Empty(failures);
            Assert.Equal("感想1", Assert.Single(model.TimelineItems).Body);
        }
        finally
        {
            await model.CancelInflightAsync().WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task RefreshOnlyTouchesTheSelectedTimelineAndRejectsALatePreviousSelectionsRefresh()
    {
        var pending = NewCompletion<ArchiveTimelineData>();
        var calls = 0;
        var oldToken = CancellationToken.None;
        var model = CreateModel(
            (id, _, _) => Task.FromResult(Detail(id)),
            (id, _, token) =>
            {
                Assert.Equal(1, id);
                calls++;
                if (calls == 1)
                    return Task.FromResult(Timeline(1, "刷新后的感想"));
                oldToken = token;
                return pending.Task;
            });
        Task oldRefresh = Task.CompletedTask;

        try
        {
            await model.SelectAsync(Archive(1)).WaitAsync(Budget);
            var screenshots = model.DetailScreenshots;
            await model.RefreshTimelineAsync(99).WaitAsync(Budget);
            Assert.Equal(0, calls);
            await model.RefreshTimelineAsync(1).WaitAsync(Budget);
            Assert.Equal("刷新后的感想", Assert.Single(model.TimelineItems).Body);
            Assert.Same(screenshots, model.DetailScreenshots);

            oldRefresh = model.RefreshTimelineAsync(1);
            await model.SelectAsync(Archive(2)).WaitAsync(Budget);
            await WaitForCancellationAsync(oldToken);
            pending.TrySetResult(Timeline(1, "旧刷新迟到"));
            await oldRefresh.WaitAsync(Budget);
            Assert.Equal("感想2", Assert.Single(model.TimelineItems).Body);
            Assert.Equal("shot-2", Assert.Single(model.DetailScreenshots).ScreenshotId);
        }
        finally
        {
            pending.TrySetResult(Timeline(1));
            await ObserveAsync(oldRefresh).WaitAsync(Budget);
            await model.CancelInflightAsync().WaitAsync(Budget);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueriesUseAvailabilityAtStartAndProjectionUsesAvailabilityAtPublication(bool initiallyAvailable)
    {
        var available = initiallyAvailable;
        var pending = NewCompletion<ArchiveDetailData>();
        var detailFlags = new List<bool>();
        var refreshFlags = new List<bool>();
        var model = new ArchiveDetailsViewModel(
            (_, includePlayback, _) =>
            {
                detailFlags.Add(includePlayback);
                return pending.Task;
            },
            (_, includePlayback, _) =>
            {
                refreshFlags.Add(includePlayback);
                return Task.FromResult(Timeline(1, includePlayback: includePlayback));
            },
            () => available);
        var loading = model.SelectAsync(Archive(1));

        try
        {
            available = !initiallyAvailable;
            pending.TrySetResult(Detail(1, includePlayback: initiallyAvailable));
            await loading.WaitAsync(Budget);
            Assert.Equal([initiallyAvailable], detailFlags);
            Assert.DoesNotContain(model.TimelineItems, item => item.Kind == "观看记录");

            available = true;
            await model.RefreshTimelineAsync(1).WaitAsync(Budget);
            Assert.Equal([true], refreshFlags);
            Assert.Contains(model.TimelineItems, item => item.Kind == "观看记录");
            available = false;
            model.ApplyPlaybackAvailability(false);
            Assert.DoesNotContain(model.TimelineItems, item => item.Kind == "观看记录");
        }
        finally
        {
            pending.TrySetResult(Detail(1));
            await ObserveAsync(loading).WaitAsync(Budget);
            await model.CancelInflightAsync().WaitAsync(Budget);
        }
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task CancellingASelectionKeepsEachQuerysTokenAliveUntilThatQueryActuallyFinishes()
    {
        var detail = NewCompletion<ArchiveDetailData>();
        var timeline = NewCompletion<ArchiveTimelineData>();
        var detailToken = CancellationToken.None;
        var timelineToken = CancellationToken.None;
        var model = CreateModel(
            (_, _, token) =>
            {
                detailToken = token;
                return detail.Task;
            },
            (_, _, token) =>
            {
                timelineToken = token;
                return timeline.Task;
            });
        var selecting = model.SelectAsync(Archive(1));
        var refreshing = model.RefreshTimelineAsync(1);
        Task stopping = Task.CompletedTask;

        try
        {
            stopping = model.CancelInflightAsync();
            Assert.False(stopping.IsCompleted);
            await Task.WhenAll(WaitForCancellationAsync(detailToken), WaitForCancellationAsync(timelineToken));
            Assert.NotNull(detailToken.WaitHandle);
            Assert.NotNull(timelineToken.WaitHandle);

            detail.TrySetResult(Detail(1));
            await selecting.WaitAsync(Budget);
            Assert.False(stopping.IsCompleted);
            Assert.NotNull(timelineToken.WaitHandle);

            timeline.TrySetResult(Timeline(1));
            await Task.WhenAll(refreshing, stopping).WaitAsync(Budget);
            Assert.Empty(model.TimelineItems);
            Assert.Empty(model.DetailScreenshots);
        }
        finally
        {
            detail.TrySetResult(Detail(1));
            timeline.TrySetResult(Timeline(1));
            await ObserveAsync(selecting, refreshing, stopping).WaitAsync(Budget);
            await model.CancelInflightAsync().WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task SelectionCallbackCanReplaceTheSelectionBeforeTheOldQueryStarts()
    {
        var queriedIds = new List<int>();
        var model = CreateModel((id, _, _) =>
        {
            queriedIds.Add(id);
            return Task.FromResult(Detail(id));
        });
        Task replacement = Task.CompletedTask;
        model.SelectionChanged += (selected, _) =>
        {
            if (selected?.Archive.AnimeId == 1)
                replacement = model.SelectAsync(Archive(2));
        };

        try
        {
            await model.SelectAsync(Archive(1)).WaitAsync(Budget);
            await replacement.WaitAsync(Budget);

            Assert.Equal([2], queriedIds);
            Assert.Equal(2, model.SelectedArchive?.Archive.AnimeId);
            Assert.Equal("shot-2", Assert.Single(model.DetailScreenshots).ScreenshotId);
        }
        finally
        {
            await ObserveAsync(replacement).WaitAsync(Budget);
            await model.CancelInflightAsync().WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task TimelineCallbackCannotLetOldScreenshotsOverwriteItsReplacementSelection()
    {
        var model = CreateModel((id, _, _) => Task.FromResult(Detail(id)));
        var detailPublications = new List<int?>();
        Task replacement = Task.CompletedTask;
        model.TimelinePublished += () =>
        {
            if (model.SelectedArchive?.Archive.AnimeId == 1)
                replacement = model.SelectAsync(Archive(2));
        };
        model.DetailsPublished += () => detailPublications.Add(model.SelectedArchive?.Archive.AnimeId);

        try
        {
            await model.SelectAsync(Archive(1)).WaitAsync(Budget);
            await replacement.WaitAsync(Budget);

            Assert.Equal([2], detailPublications);
            Assert.Equal("感想2", Assert.Single(model.TimelineItems).Body);
            Assert.Equal("shot-2", Assert.Single(model.DetailScreenshots).ScreenshotId);
        }
        finally
        {
            await ObserveAsync(replacement).WaitAsync(Budget);
            await model.CancelInflightAsync().WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task RetiredCancellationFailureReachesTheWaiterOnlyAfterTheQueryHasFinished()
    {
        var pending = NewCompletion<ArchiveDetailData>();
        var callbackStarted = NewCompletion();
        var failure = new InvalidOperationException("取消回调失败");
        var token = CancellationToken.None;
        var model = CreateModel((id, _, cancellationToken) =>
        {
            if (id != 1)
                return Task.FromResult(Detail(id));
            token = cancellationToken;
            return pending.Task;
        });
        var selecting = model.SelectAsync(Archive(1));
        using var registration = token.Register(() =>
        {
            callbackStarted.TrySetResult();
            throw failure;
        });
        Task stopping = Task.CompletedTask;

        try
        {
            await model.SelectAsync(Archive(2)).WaitAsync(Budget);
            await callbackStarted.Task.WaitAsync(Budget);
            stopping = model.CancelInflightAsync();
            Assert.False(stopping.IsCompleted);

            pending.TrySetResult(Detail(1));
            await selecting.WaitAsync(Budget);
            var error = await Assert.ThrowsAsync<AggregateException>(() => stopping.WaitAsync(Budget));
            Assert.Contains(failure, error.Flatten().InnerExceptions);
            Assert.Equal(2, model.SelectedArchive?.Archive.AnimeId);
            Assert.Equal("shot-2", Assert.Single(model.DetailScreenshots).ScreenshotId);
            await model.CancelInflightAsync().WaitAsync(Budget);
        }
        finally
        {
            pending.TrySetResult(Detail(1));
            await ObserveAsync(selecting, stopping).WaitAsync(Budget);
            await model.CancelInflightAsync().WaitAsync(Budget);
        }
    }

    private static ArchiveDetailsViewModel CreateModel(
        Func<int, bool, CancellationToken, Task<ArchiveDetailData>> loadDetail,
        Func<int, bool, CancellationToken, Task<ArchiveTimelineData>>? loadTimeline = null)
        => new(
            loadDetail,
            loadTimeline ?? ((id, _, _) => Task.FromResult(Timeline(id))),
            () => false);

    private static ArchiveListItem Archive(int id)
        => new(new AnimeArchive(id, $"作品{id}", 8, "笔记", RecordedAt, RecordedAt), ["标签"], 1, 1, null);

    private static ArchiveDetailData Detail(int id, string? body = null, bool includePlayback = false)
        => new(Timeline(id, body, includePlayback), [Screenshot($"shot-{id}", id, true)]);

    private static ArchiveTimelineData Timeline(int id, string? body = null, bool includePlayback = false)
        => new(
            [new ArchiveEntry($"entry-{id}", id, RecordedAt, 2, body ?? $"感想{id}", RecordedAt, RecordedAt)],
            includePlayback ? [new WatchHistoryItem($"watch-{id}", id, $"作品{id}", RecordedAt.AddDays(1), 1, 2, 48, "观看备注", true)] : [],
            []);

    private static AnimeScreenshot Screenshot(string id, int animeId, bool exists)
        => new(id, $"{id}.png", "", RecordedAt, "测试窗口", "测试进程", 1920, 1080,
            animeId, $"作品{animeId}", 2, 90, "截图备注", exists);

    private static Exception KnownFailure(string kind) => kind switch
    {
        "io" => new IOException("当前详情读取失败"),
        "state" => new InvalidOperationException("当前详情状态失败"),
        "sqlite" => new SqliteException("当前详情数据库失败", 1),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static TaskCompletionSource<T> NewCompletion<T>()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource NewCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitForCancellationAsync(CancellationToken token)
    {
        var cancelled = NewCompletion();
        using var registration = token.Register(() => cancelled.TrySetResult());
        await cancelled.Task.WaitAsync(Budget);
    }

    private static async Task ObserveAsync(params Task[] tasks)
    {
        // 调用方已断言故障；此处只观察实际任务完成，等待预算仍由调用方控制。
        await Task.WhenAll(tasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }
}
