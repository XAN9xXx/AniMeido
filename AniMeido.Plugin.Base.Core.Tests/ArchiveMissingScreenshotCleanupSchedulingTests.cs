using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;

namespace AniMeido.Tests;

public sealed class ArchiveMissingScreenshotCleanupSchedulingTests : IAsyncLifetime
{
    private static readonly TimeSpan ReturnBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CompletionBudget = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(8));
    private readonly PersonalDatabaseFixture _database = new();
    private SqliteConnectionFactory Factory => _database.Factory;

    public Task InitializeAsync() => _database.InitializeAsync();

    public Task DisposeAsync() => _database.DisposeAsync();

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task Cleanup_ReturnsWhileSqliteIsLocked_AndKeepsAdmissionUntilItFinishes()
    {
        var (archive, original) = await SeedAsync();
        using var blocker = await _database.LockExclusiveAsync();
        var context = new PausedUiContext();
        var invocation = SchedulingTestThread.InvokeAsync<Task<int>>(() => archive.RemoveMissingScreenshotRecordsAsync(), context);
        PersonalDataAccess.Operation? import = null;

        try
        {
            var pending = await invocation.WaitAsync(ReturnBudget);
            Assert.False(pending.IsCompleted);
            import = Factory.Access.BeginExclusive();
            var drained = import.WaitForDrainAsync(CancellationToken.None);
            Assert.False(drained.IsCompleted);

            blocker.Dispose();
            Assert.Equal(2, await pending.WaitAsync(CompletionBudget));
            await drained.WaitAsync(CompletionBudget);
            Assert.Equal(0, context.PostCount);
        }
        finally
        {
            blocker.Dispose();
            context.Resume();
            try
            {
                await FinishInvocationAsync(invocation);
            }
            finally
            {
                import?.Dispose();
            }
        }

        await AssertCleanedAsync(archive, original);
    }

    [Fact]
    public async Task Cleanup_CountsOnlyMissingRecords_AndKeepsTheExistingFileAndItsMetadata()
    {
        var (archive, original) = await SeedAsync();

        var removed = await archive.RemoveMissingScreenshotRecordsAsync();

        Assert.Equal(2, removed);
        await AssertCleanedAsync(archive, original);
        Assert.Equal(0, await archive.RemoveMissingScreenshotRecordsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_WithNoMissingFilesReturnsZero_AndKeepsRecords(bool hasExistingRecord)
    {
        var archive = new ArchiveService(Factory);
        if (hasExistingRecord)
        {
            await archive.InsertScreenshotAsync(ExistingScreenshot());
        }
        var original = await archive.GetScreenshotsAsync();

        Assert.Equal(0, await archive.RemoveMissingScreenshotRecordsAsync());

        Assert.Equal(original, await archive.GetScreenshotsAsync());
        Assert.True(File.Exists(Factory.DatabasePath));
        await Factory.StopAsync().WaitAsync(CompletionBudget);
    }

    [Fact]
    public async Task Cleanup_PreCancelledRequestKeepsRecords_AndAllowsTheNextCleanup()
    {
        var (archive, original) = await SeedAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            archive.RemoveMissingScreenshotRecordsAsync(cancellation.Token));

        Assert.Equal(original, await archive.GetScreenshotsAsync());
        Assert.Equal(2, await archive.RemoveMissingScreenshotRecordsAsync());
        await AssertCleanedAsync(archive, original);
        await Factory.StopAsync().WaitAsync(CompletionBudget);
    }

    [Fact]
    public async Task Cleanup_SecondDeleteFailureRollsBackTheFirst_AndAllowsRetry()
    {
        var (archive, original) = await SeedAsync();
        using (var connection = await Factory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER fail_second_missing_screenshot BEFORE DELETE ON screenshots
                WHEN OLD.ScreenshotId = 'missing-second'
                BEGIN SELECT RAISE(ABORT, 'synthetic missing screenshot failure'); END
                """;
            await command.ExecuteNonQueryAsync();
        }

        var error = await Assert.ThrowsAsync<SqliteException>(() => archive.RemoveMissingScreenshotRecordsAsync());

        Assert.Contains("synthetic missing screenshot failure", error.Message);
        Assert.Equal(original, await archive.GetScreenshotsAsync());
        Assert.True(File.Exists(Factory.DatabasePath));
        using (var connection = await Factory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TRIGGER fail_second_missing_screenshot";
            await command.ExecuteNonQueryAsync();
        }

        Assert.Equal(2, await archive.RemoveMissingScreenshotRecordsAsync());
        await AssertCleanedAsync(archive, original);
        await Factory.StopAsync().WaitAsync(CompletionBudget);
    }

    [Fact]
    public async Task Cleanup_DoesNotBypassAnUnrelatedExclusiveOperation()
    {
        var (archive, original) = await SeedAsync();
        using (var import = Factory.Access.BeginExclusive())
        {
            await import.WaitForDrainAsync(CancellationToken.None);
            Task<int> cleanup;
            using (ExecutionContext.SuppressFlow())
            {
                cleanup = Task.Run(() => archive.RemoveMissingScreenshotRecordsAsync());
            }

            await Assert.ThrowsAsync<PersonalDataBusyException>(() => cleanup);
            Assert.Equal(original, await archive.GetScreenshotsAsync());
        }

        Assert.Equal(2, await archive.RemoveMissingScreenshotRecordsAsync());
        await AssertCleanedAsync(archive, original);
    }

    [Fact]
    public async Task Cleanup_InnerCompletionDoesNotReleaseTheOuterBusinessOperation()
    {
        var (archive, original) = await SeedAsync();
        var cleaned = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishOuter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Run(async () =>
        {
            using var outer = Factory.Access.BeginWrite();
            cleaned.TrySetResult(await archive.RemoveMissingScreenshotRecordsAsync());
            await finishOuter.Task;
        });

        try
        {
            Assert.Equal(2, await cleaned.Task.WaitAsync(CompletionBudget));
            using var import = Factory.Access.BeginExclusive();
            var drained = import.WaitForDrainAsync(CancellationToken.None);
            Assert.False(drained.IsCompleted);
            finishOuter.TrySetResult();
            await writer.WaitAsync(CompletionBudget);
            await drained.WaitAsync(CompletionBudget);
        }
        finally
        {
            finishOuter.TrySetResult();
            await writer.WaitAsync(CompletionBudget);
        }

        await AssertCleanedAsync(archive, original);
    }

    private async Task<(ArchiveService Archive, IReadOnlyList<AnimeScreenshot> Original)> SeedAsync()
    {
        var archive = new ArchiveService(Factory);
        await archive.InsertScreenshotAsync(ExistingScreenshot());
        await archive.InsertScreenshotAsync(ExistingScreenshot() with
        {
            ScreenshotId = "missing-first",
            FilePath = LocalPath("missing-first.png"),
            CapturedAt = At.AddMinutes(2),
            AnimeId = null,
            AnimeTitle = null,
            EpisodeNumber = null,
            PlaybackPositionSeconds = null,
            FileExists = false,
        });
        await archive.InsertScreenshotAsync(ExistingScreenshot() with
        {
            ScreenshotId = "missing-second",
            FilePath = LocalPath("missing-second.png"),
            CapturedAt = At.AddMinutes(1),
            AnimeId = -7,
            AnimeTitle = "Legacy title",
            EpisodeNumber = 4,
            PlaybackPositionSeconds = 84.5,
            FileExists = false,
        });
        return (archive, await archive.GetScreenshotsAsync());
    }

    private async Task AssertCleanedAsync(ArchiveService archive, IReadOnlyList<AnimeScreenshot> original)
    {
        var kept = Assert.Single(await archive.GetScreenshotsAsync());
        Assert.Equal(original.Single(item => item.ScreenshotId == "existing"), kept);
        Assert.True(File.Exists(Factory.DatabasePath));
    }

    private AnimeScreenshot ExistingScreenshot()
        // 存在性探测使用夹具自己的数据库，不创建截图，也不修改文件内容。
        => new("existing", Factory.DatabasePath, "synthetic stored hash", At, "Original window", "original-process",
            1920, 1080, 42, "Original title", 3, 42.5, "Original context", true);

    private string LocalPath(string name) => Path.Combine(Path.GetDirectoryName(Factory.DatabasePath)!, name);

    private static async Task FinishInvocationAsync(Task<Task<int>> invocation)
    {
        var pending = await invocation.WaitAsync(CompletionBudget);
        try
        {
            await pending.WaitAsync(CompletionBudget);
        }
        catch (SqliteException) when (pending.IsFaulted)
        {
            // 锁失败由正文断言；仍等待真实操作结束后再释放测试夹具。
        }
    }
}
