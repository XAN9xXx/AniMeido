using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;

namespace AniMeido.Tests;

public sealed class ArchiveScreenshotWriteSchedulingTests : IAsyncLifetime
{
    private static readonly TimeSpan ReturnBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CompletionBudget = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset CapturedAt = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly PersonalDatabaseFixture _database = new();
    private SqliteConnectionFactory Factory => _database.Factory;

    public Task InitializeAsync() => _database.InitializeAsync();

    public Task DisposeAsync() => _database.DisposeAsync();

    public enum WriteKind
    {
        Metadata,
        AddTags,
        RemoveTag,
    }

    [Theory]
    [InlineData(WriteKind.Metadata)]
    [InlineData(WriteKind.AddTags)]
    [InlineData(WriteKind.RemoveTag)]
    [Trait("Category", "Mechanism")]
    public async Task ScreenshotWrite_ReturnsWhileSqliteIsLocked_AndKeepsAdmissionUntilItFinishes(WriteKind kind)
    {
        var (archive, originals) = await SeedAsync();
        using var blocker = await _database.LockExclusiveAsync();
        var context = new PausedUiContext();
        var invocation = SchedulingTestThread.InvokeAsync<Task>(() => RunAsync(archive, kind), context);
        PersonalDataAccess.Operation? import = null;

        try
        {
            var pending = await invocation.WaitAsync(ReturnBudget);
            Assert.False(pending.IsCompleted);
            import = Factory.Access.BeginExclusive();
            var drained = import.WaitForDrainAsync(CancellationToken.None);
            Assert.False(drained.IsCompleted);

            blocker.Dispose();
            await pending.WaitAsync(CompletionBudget);
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

        await AssertSavedAsync(archive, originals, kind);
    }

    [Theory]
    [InlineData(WriteKind.Metadata)]
    [InlineData(WriteKind.AddTags)]
    [InlineData(WriteKind.RemoveTag)]
    public async Task ScreenshotWrite_PreCancelledRequestKeepsRecordsAndReleasesAdmission(WriteKind kind)
    {
        var (archive, originals) = await SeedAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(archive, kind, cancellation.Token));

        await AssertUnchangedAsync(archive, originals);
        await Factory.StopAsync().WaitAsync(CompletionBudget);
    }

    [Theory]
    [InlineData(WriteKind.Metadata)]
    [InlineData(WriteKind.AddTags)]
    [InlineData(WriteKind.RemoveTag)]
    public async Task ScreenshotWrite_SqlFailureKeepsTheCompleteBatchAndAllowsTheNextWrite(WriteKind kind)
    {
        var (archive, originals) = await SeedAsync();
        var (table, action, condition) = kind switch
        {
            WriteKind.Metadata => ("screenshots", "UPDATE", ""),
            WriteKind.AddTags => ("screenshot_personal_tags", "INSERT", "WHEN NEW.ScreenshotId = 'two'"),
            WriteKind.RemoveTag => ("screenshot_personal_tags", "DELETE", ""),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        using (var connection = await Factory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                CREATE TRIGGER fail_screenshot_write BEFORE {action} ON {table}
                {condition}
                BEGIN SELECT RAISE(ABORT, 'synthetic screenshot write failure'); END
                """;
            await command.ExecuteNonQueryAsync();
        }

        var error = await Assert.ThrowsAsync<SqliteException>(() => RunAsync(archive, kind));
        Assert.Contains("synthetic screenshot write failure", error.Message);
        await AssertUnchangedAsync(archive, originals);
        using (var connection = await Factory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            // 批量绑定的第二张失败时，第一张绑定和新标签本身也必须回滚。
            command.CommandText = "SELECT COUNT(*) FROM personal_tags WHERE Name = 'next'";
            Assert.Equal(0L, await command.ExecuteScalarAsync());
            command.CommandText = "DROP TRIGGER fail_screenshot_write";
            await command.ExecuteNonQueryAsync();
        }

        await RunAsync(archive, kind).WaitAsync(CompletionBudget);
        await AssertSavedAsync(archive, originals, kind);
        await Factory.StopAsync().WaitAsync(CompletionBudget);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddTags_EmptyWorkStillSucceedsWithAPreCancelledToken(bool emptyIds)
    {
        var (archive, originals) = await SeedAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await archive.AddScreenshotTagsAsync(
            emptyIds ? [] : ["one", "two"],
            emptyIds ? ["next"] : ["", "  "],
            cancellation.Token).WaitAsync(CompletionBudget);

        await AssertUnchangedAsync(archive, originals);
        await Factory.StopAsync().WaitAsync(CompletionBudget);
    }

    private static Task RunAsync(ArchiveService archive, WriteKind kind, CancellationToken ct = default)
        => kind switch
        {
            WriteKind.Metadata => archive.UpdateScreenshotMetadataAsync("one", -7, " New title ", 4, " New note ", ct),
            WriteKind.AddTags => archive.AddScreenshotTagsAsync(["one", "two"], [" next ", "NEXT", "", "keep"], ct),
            WriteKind.RemoveTag => archive.RemoveScreenshotTagAsync("one", "REMOVE", ct),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private async Task<(ArchiveService Archive, IReadOnlyDictionary<string, AnimeScreenshot> Originals)> SeedAsync()
    {
        var archive = new ArchiveService(Factory);
        foreach (var id in new[] { "one", "two" })
        {
            await archive.InsertScreenshotAsync(new AnimeScreenshot(
                id, Path.Combine(Path.GetDirectoryName(Factory.DatabasePath)!, id + ".png"),
                "synthetic stored hash", CapturedAt, "Original window", "process", 640, 360,
                42, "Original title", 1, 12.5, "Original note", false));
        }
        await archive.AddScreenshotTagsAsync(["one", "two"], ["keep", "remove"]);
        return (archive, (await archive.GetScreenshotsAsync()).ToDictionary(item => item.ScreenshotId));
    }

    private static async Task AssertUnchangedAsync(
        ArchiveService archive,
        IReadOnlyDictionary<string, AnimeScreenshot> originals)
    {
        var actual = (await archive.GetScreenshotsAsync()).ToDictionary(item => item.ScreenshotId);
        Assert.Equal(originals.Count, actual.Count);
        foreach (var item in originals)
        {
            Assert.Equal(item.Value, actual[item.Key]);
            Assert.Equal(["keep", "remove"], await archive.GetScreenshotTagsAsync(item.Key));
        }
    }

    private static async Task AssertSavedAsync(
        ArchiveService archive,
        IReadOnlyDictionary<string, AnimeScreenshot> originals,
        WriteKind kind)
    {
        var actual = (await archive.GetScreenshotsAsync()).ToDictionary(item => item.ScreenshotId);
        Assert.Equal(originals.Count, actual.Count);
        foreach (var item in originals)
        {
            var expected = kind == WriteKind.Metadata && item.Key == "one"
                ? item.Value with { AnimeId = -7, AnimeTitle = "New title", EpisodeNumber = 4, ContextNote = "New note" }
                : item.Value;
            Assert.Equal(expected, actual[item.Key]);
            string[] tags = kind switch
            {
                WriteKind.AddTags => ["keep", "next", "remove"],
                WriteKind.RemoveTag when item.Key == "one" => ["keep"],
                _ => ["keep", "remove"],
            };
            Assert.Equal(tags, await archive.GetScreenshotTagsAsync(item.Key));
        }
    }

    private static async Task FinishInvocationAsync(Task<Task> invocation)
    {
        var pending = await invocation.WaitAsync(CompletionBudget);
        try
        {
            await pending.WaitAsync(CompletionBudget);
        }
        catch (SqliteException) when (pending.IsFaulted)
        {
            // 正文负责 SQL 错误断言；等待超时仍应暴露，不能当作实际任务已结束。
        }
    }
}
