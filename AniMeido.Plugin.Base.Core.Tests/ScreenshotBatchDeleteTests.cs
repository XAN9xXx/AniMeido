using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;

namespace AniMeido.Tests;

public sealed class ScreenshotBatchDeleteTests : IAsyncLifetime
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private readonly PersonalDatabaseFixture _database = new();
    private SqliteConnectionFactory Factory => _database.Factory;
    private ArchiveService Archive => new(Factory);
    private string Root => Path.GetDirectoryName(Factory.DatabasePath)!;

    public Task InitializeAsync() => _database.InitializeAsync();
    public Task DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task EmptyBatchReturnsZeroCounts()
    {
        var recycle = new TemporaryRecycleBin(Root);

        var result = await Service(recycle).DeleteManyAsync([]).WaitAsync(Budget);

        Assert.Equal((0, 0), result);
        Assert.Empty(recycle.Calls);
    }

    [Fact]
    public async Task BatchDeletesInOrderAndCountsMissingFilesAsDeletedRecords()
    {
        var first = await SeedAsync("first");
        var missing = await SeedAsync("missing", exists: false);
        var last = await SeedAsync("last");
        var recycle = new TemporaryRecycleBin(Root);

        var result = await Service(recycle).DeleteManyAsync([first, missing, last]).WaitAsync(Budget);

        Assert.Equal(3, result.Deleted);
        Assert.Equal(0, result.Failed);
        Assert.Equal(new[] { first.FilePath, last.FilePath }, recycle.Calls);
        Assert.Empty(await Archive.GetScreenshotsAsync());
        Assert.False(File.Exists(first.FilePath));
        Assert.False(File.Exists(last.FilePath));
        Assert.True(File.Exists(recycle.Destination(first.FilePath)));
        Assert.True(File.Exists(recycle.Destination(last.FilePath)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileFailureKeepsItsRecordAndContinuesToTheNextItem(bool accessDenied)
    {
        var first = await SeedAsync("failed");
        var next = await SeedAsync("next");
        Exception failure = accessDenied
            ? new UnauthorizedAccessException("fixture recycle denied")
            : new IOException("fixture recycle failed");
        var recycle = new TemporaryRecycleBin(Root)
        {
            BeforeMove = path =>
            {
                if (path == first.FilePath)
                    throw failure;
            },
        };

        var result = await Service(recycle).DeleteManyAsync([first, next]).WaitAsync(Budget);

        Assert.Equal(1, result.Deleted);
        Assert.Equal(1, result.Failed);
        Assert.Equal(new[] { first.FilePath, next.FilePath }, recycle.Calls);
        Assert.Equal(first.ScreenshotId, Assert.Single(await Archive.GetScreenshotsAsync()).ScreenshotId);
        Assert.True(File.Exists(first.FilePath));
        Assert.False(File.Exists(recycle.Destination(first.FilePath)));
        Assert.False(File.Exists(next.FilePath));
        Assert.True(File.Exists(recycle.Destination(next.FilePath)));
    }

    [Fact]
    public async Task SqlFailurePropagatesWithoutRollingBackEarlierFilesOrRecords()
    {
        var first = await SeedAsync("first");
        var failed = await SeedAsync("sql-failed");
        var untouched = await SeedAsync("untouched");
        await using (var connection = await Factory.OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER reject_fixture_screenshot_delete BEFORE DELETE ON screenshots
                WHEN OLD.ScreenshotId='sql-failed'
                BEGIN SELECT RAISE(ABORT,'fixture screenshot delete failure'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var recycle = new TemporaryRecycleBin(Root);

        var failure = await Assert.ThrowsAsync<SqliteException>(() => Service(recycle)
            .DeleteManyAsync([first, failed, untouched]).WaitAsync(Budget));

        Assert.Contains("fixture screenshot delete failure", failure.Message);
        Assert.Equal(new[] { first.FilePath, failed.FilePath }, recycle.Calls);
        Assert.Equal(new[] { failed.ScreenshotId, untouched.ScreenshotId },
            (await Archive.GetScreenshotsAsync()).Select(item => item.ScreenshotId).Order());
        Assert.False(File.Exists(first.FilePath));
        Assert.False(File.Exists(failed.FilePath));
        Assert.True(File.Exists(recycle.Destination(first.FilePath)));
        Assert.True(File.Exists(recycle.Destination(failed.FilePath)));
        Assert.True(File.Exists(untouched.FilePath));
    }

    [Fact]
    public async Task UnexpectedFileFailurePropagatesWithoutTryingTheNextItem()
    {
        var first = await SeedAsync("first");
        var next = await SeedAsync("next");
        var failure = new InvalidOperationException("fixture unexpected recycle failure");
        var recycle = new TemporaryRecycleBin(Root) { BeforeMove = _ => throw failure };

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(recycle)
            .DeleteManyAsync([first, next]).WaitAsync(Budget));

        Assert.Same(failure, actual);
        Assert.Equal(new[] { first.FilePath }, recycle.Calls);
        Assert.Equal(2, (await Archive.GetScreenshotsAsync()).Count);
        Assert.True(File.Exists(first.FilePath));
        Assert.True(File.Exists(next.FilePath));
    }

    [Fact]
    public async Task CancellationDuringRecyclePropagatesAndKeepsTheNotYetDeletedRecords()
    {
        var first = await SeedAsync("first");
        var next = await SeedAsync("next");
        using var cancellation = new CancellationTokenSource();
        var recycle = new TemporaryRecycleBin(Root) { BeforeMove = _ => cancellation.Cancel() };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(recycle)
            .DeleteManyAsync([first, next], cancellation.Token).WaitAsync(Budget));

        Assert.Equal(new[] { first.FilePath }, recycle.Calls);
        Assert.Equal(new[] { first.ScreenshotId, next.ScreenshotId },
            (await Archive.GetScreenshotsAsync()).Select(item => item.ScreenshotId).Order());
        Assert.False(File.Exists(first.FilePath));
        Assert.True(File.Exists(recycle.Destination(first.FilePath)));
        Assert.True(File.Exists(next.FilePath));
    }

    [Fact]
    public async Task PreCancelledBatchDoesNotTouchFilesOrRecords()
    {
        var item = await SeedAsync("retained");
        var recycle = new TemporaryRecycleBin(Root);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(recycle)
            .DeleteManyAsync([item], cancellation.Token).WaitAsync(Budget));

        Assert.Empty(recycle.Calls);
        Assert.True(File.Exists(item.FilePath));
        Assert.Equal(item.ScreenshotId, Assert.Single(await Archive.GetScreenshotsAsync()).ScreenshotId);
    }

    [Fact]
    public async Task ImportExclusionRejectsTheBatchBeforeTheFirstFileMove()
    {
        var item = await SeedAsync("retained");
        var recycle = new TemporaryRecycleBin(Root);
        using var import = Factory.Access.BeginExclusive();
        await import.WaitForDrainAsync(CancellationToken.None).WaitAsync(Budget);
        Task deleting;
        using (ExecutionContext.SuppressFlow())
            deleting = Task.Run(() => Service(recycle).DeleteManyAsync([item]));
        try
        {
            await Assert.ThrowsAsync<PersonalDataBusyException>(() => deleting.WaitAsync(Budget));
            Assert.Empty(recycle.Calls);
            Assert.True(File.Exists(item.FilePath));
            Assert.Equal(item.ScreenshotId, Assert.Single(await Archive.GetScreenshotsAsync()).ScreenshotId);
        }
        finally
        {
            await ObserveAsync(deleting).WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task BatchKeepsItsAdmissionAcrossItemsWhileAnImportWaits()
    {
        var first = await SeedAsync("first");
        var next = await SeedAsync("next");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        Task<PersonalDataAccess.Operation>? startingImport = null;
        PersonalDataAccess.Operation? import = null;
        Task? drained = null;
        var recycle = new TemporaryRecycleBin(Root)
        {
            BeforeMove = path =>
            {
                if (path != first.FilePath)
                    return;

                using (ExecutionContext.SuppressFlow())
                    startingImport = Task.Run(Factory.Access.BeginExclusive);
                import = startingImport.WaitAsync(Budget).GetAwaiter().GetResult();
                drained = import.WaitForDrainAsync(CancellationToken.None);
                entered.TrySetResult();
                if (!release.Wait(Budget))
                    throw new TimeoutException("The test did not release the first recycle action.");
            },
        };
        var deleting = Task.Run(() => Service(recycle).DeleteManyAsync([first, next]));
        try
        {
            await entered.Task.WaitAsync(Budget);
            Assert.NotNull(drained);
            Assert.False(drained.IsCompleted);
            Assert.False(deleting.IsCompleted);

            release.Set();
            var result = await deleting.WaitAsync(Budget);

            Assert.Equal(2, result.Deleted);
            Assert.Equal(0, result.Failed);
            await drained.WaitAsync(Budget);
            Assert.Equal(new[] { first.FilePath, next.FilePath }, recycle.Calls);
        }
        finally
        {
            release.Set();
            try
            {
                await ObserveAsync(deleting).WaitAsync(Budget);
            }
            finally
            {
                if (startingImport is not null)
                {
                    import ??= await startingImport.WaitAsync(Budget);
                    import.Dispose();
                }
            }
        }
        Assert.Empty(await Archive.GetScreenshotsAsync());
        Assert.True(File.Exists(recycle.Destination(first.FilePath)));
        Assert.True(File.Exists(recycle.Destination(next.FilePath)));
    }

    private ScreenshotArchiveService Service(IScreenshotRecycleBin recycle)
        => new(Archive, null!, null!, recycle);

    private async Task<AnimeScreenshot> SeedAsync(string id, bool exists = true)
    {
        var path = Path.Combine(Root, id + ".png");
        if (exists)
            await File.WriteAllTextAsync(path, "synthetic screenshot " + id);
        var screenshot = new AnimeScreenshot(id, path, "synthetic-not-a-hash",
            new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero), "fixture", "fixture",
            1, 1, null, null, null, null, "", exists);
        await Archive.InsertScreenshotAsync(screenshot);
        return screenshot;
    }

    private static async Task ObserveAsync(Task task)
        => await task.ConfigureAwait(
            ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);

    private sealed class TemporaryRecycleBin : IScreenshotRecycleBin
    {
        private readonly string _directory;
        internal List<string> Calls { get; } = [];
        internal Action<string>? BeforeMove { get; init; }

        internal TemporaryRecycleBin(string root)
        {
            _directory = Path.Combine(root, "recycled");
            Directory.CreateDirectory(_directory);
        }

        internal string Destination(string path) => Path.Combine(_directory, Path.GetFileName(path));

        public void MoveToRecycleBin(string path)
        {
            Calls.Add(path);
            BeforeMove?.Invoke(path);
            File.Move(path, Destination(path));
        }
    }
}
