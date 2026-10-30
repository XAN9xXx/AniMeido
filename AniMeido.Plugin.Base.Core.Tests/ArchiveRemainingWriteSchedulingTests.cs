using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;

namespace AniMeido.Tests;

public sealed class ArchiveRemainingWriteSchedulingTests : IAsyncLifetime
{
    private static readonly TimeSpan ReturnBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CompletionBudget = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(8));
    private readonly PersonalDatabaseFixture _database = new();
    private SqliteConnectionFactory Factory => _database.Factory;

    public Task InitializeAsync() => _database.InitializeAsync();

    public Task DisposeAsync() => _database.DisposeAsync();

    public enum WriteKind
    {
        AnimeTags,
        ManualWatch,
        InsertScreenshot,
        DeleteScreenshot,
        ScreenshotSettings,
    }

    [Theory]
    [InlineData(WriteKind.AnimeTags)]
    [InlineData(WriteKind.ManualWatch)]
    [InlineData(WriteKind.InsertScreenshot)]
    [InlineData(WriteKind.DeleteScreenshot)]
    [InlineData(WriteKind.ScreenshotSettings)]
    [Trait("Category", "Mechanism")]
    public async Task Write_ReturnsWhileSqliteIsLocked_AndKeepsAdmissionUntilItFinishes(WriteKind kind)
    {
        var (archive, original) = await SeedAsync();
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

        await AssertSavedAsync(archive, original, kind);
    }

    [Theory]
    [InlineData(WriteKind.AnimeTags)]
    [InlineData(WriteKind.ManualWatch)]
    [InlineData(WriteKind.InsertScreenshot)]
    [InlineData(WriteKind.DeleteScreenshot)]
    [InlineData(WriteKind.ScreenshotSettings)]
    public async Task Write_PreCancelledRequestKeepsRecordsAndReleasesAdmission(WriteKind kind)
    {
        var (archive, original) = await SeedAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(archive, kind, cancellation.Token));

        AssertSnapshot(original, await ReadSnapshotAsync(archive));
        await Factory.StopAsync().WaitAsync(CompletionBudget);
    }

    [Theory]
    [InlineData(WriteKind.AnimeTags)]
    [InlineData(WriteKind.ManualWatch)]
    [InlineData(WriteKind.InsertScreenshot)]
    [InlineData(WriteKind.DeleteScreenshot)]
    [InlineData(WriteKind.ScreenshotSettings)]
    public async Task Write_SqlFailureKeepsRecordsAndAllowsTheNextWrite(WriteKind kind)
    {
        var (archive, original) = await SeedAsync();
        var (table, action) = kind switch
        {
            WriteKind.AnimeTags => ("anime_personal_tags", "INSERT"),
            WriteKind.ManualWatch => ("manual_watch_events", "INSERT"),
            WriteKind.InsertScreenshot => ("screenshots", "INSERT"),
            WriteKind.DeleteScreenshot => ("screenshots", "DELETE"),
            WriteKind.ScreenshotSettings => ("config", "INSERT"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        using (var connection = await Factory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                CREATE TRIGGER fail_remaining_archive_write BEFORE {action} ON {table}
                BEGIN SELECT RAISE(ABORT, 'synthetic remaining archive write failure'); END
                """;
            await command.ExecuteNonQueryAsync();
        }

        var error = await Assert.ThrowsAsync<SqliteException>(() => RunAsync(archive, kind));
        Assert.Contains("synthetic remaining archive write failure", error.Message);
        AssertSnapshot(original, await ReadSnapshotAsync(archive));
        using (var connection = await Factory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            if (kind == WriteKind.AnimeTags)
            {
                // 删除旧绑定和创建新标签都必须随失败的绑定一起回滚。
                command.CommandText = "SELECT COUNT(*) FROM personal_tags WHERE Name = 'new'";
                Assert.Equal(0L, await command.ExecuteScalarAsync());
            }
            command.CommandText = "DROP TRIGGER fail_remaining_archive_write";
            await command.ExecuteNonQueryAsync();
        }

        await RunAsync(archive, kind).WaitAsync(CompletionBudget);
        await AssertSavedAsync(archive, original, kind);
        await Factory.StopAsync().WaitAsync(CompletionBudget);
    }

    [Fact]
    public async Task InsertScreenshot_InnerCompletionDoesNotReleaseTheOuterBusinessOperation()
    {
        var (archive, _) = await SeedAsync();
        var inserted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishOuter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Run(async () =>
        {
            using var outer = Factory.Access.BeginWrite();
            await archive.InsertScreenshotAsync(NewScreenshot());
            inserted.TrySetResult();
            await finishOuter.Task;
        });

        try
        {
            await inserted.Task.WaitAsync(CompletionBudget);
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

        Assert.Equal(NewScreenshot(), (await archive.GetScreenshotsAsync()).Single(item => item.ScreenshotId == "new-shot"));
    }

    [Fact]
    public async Task AnimeTags_KeepCallerNormalizationAndTheExistingThirtyTagLimit()
    {
        var (archive, _) = await SeedAsync();
        var tags = new[] { " First ", "FIRST", "", new string('x', 51) }
            .Concat(Enumerable.Range(0, 40).Select(index => $" tag{index:00} "));

        await archive.SetAnimeTagsAsync(42, tags);

        Assert.Equal(new[] { "First" }.Concat(Enumerable.Range(0, 29).Select(index => $"tag{index:00}")),
            await archive.GetAnimeTagsAsync(42));
    }

    private Task RunAsync(ArchiveService archive, WriteKind kind, CancellationToken ct = default)
        => kind switch
        {
            WriteKind.AnimeTags => archive.SetAnimeTagsAsync(42, [" new ", "NEW", "", new string('x', 51), "keep"], ct),
            WriteKind.ManualWatch => archive.AddManualWatchEventAsync(NewWatch(), ct),
            WriteKind.InsertScreenshot => archive.InsertScreenshotAsync(NewScreenshot(), ct),
            WriteKind.DeleteScreenshot => archive.DeleteScreenshotRecordAsync("old-shot", ct),
            WriteKind.ScreenshotSettings => archive.SaveScreenshotSettingsAsync(NewSettings(), ct),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private async Task<(ArchiveService Archive, Snapshot Original)> SeedAsync()
    {
        var archive = new ArchiveService(Factory);
        await archive.UpsertArchiveAsync(42, "Original archive", 8.5, "Original summary");
        await archive.SetAnimeTagsAsync(42, ["keep", "old"]);
        await archive.AddManualWatchEventAsync(NewWatch() with
        {
            EventId = "old-watch", OccurredAt = At.AddDays(-1), Note = "original note", CreatedAt = At.AddDays(-1),
        });
        await archive.InsertScreenshotAsync(NewScreenshot() with
        {
            ScreenshotId = "old-shot", FilePath = LocalPath("old.png"), CapturedAt = At.AddDays(-1),
            AnimeId = 42, AnimeTitle = "Original title", EpisodeNumber = 3, PlaybackPositionSeconds = 12.5,
        });
        await archive.SaveScreenshotSettingsAsync(new ScreenshotSettings(true, LocalPath("old-directory"), false, true));
        return (archive, await ReadSnapshotAsync(archive));
    }

    private async Task AssertSavedAsync(ArchiveService archive, Snapshot original, WriteKind kind)
    {
        var expected = kind switch
        {
            WriteKind.AnimeTags => original with { Tags = new[] { "keep", "new" } },
            WriteKind.ManualWatch => original with
            {
                History = new[] { new WatchHistoryItem("new-watch", 42, "Manual title", At, 2, 4, 72, "new note", true) }
                    .Concat(original.History).ToArray(),
            },
            WriteKind.InsertScreenshot => original with { Screenshots = new[] { NewScreenshot() }.Concat(original.Screenshots).ToArray() },
            WriteKind.DeleteScreenshot => original with { Screenshots = Array.Empty<AnimeScreenshot>() },
            WriteKind.ScreenshotSettings => original with { Settings = NewSettings() },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        AssertSnapshot(expected, await ReadSnapshotAsync(archive));
        if (kind == WriteKind.ManualWatch)
        {
            using var connection = await Factory.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT CreatedAt FROM manual_watch_events WHERE EventId = 'new-watch'";
            var stored = Assert.IsType<string>(await command.ExecuteScalarAsync());
            Assert.Equal(NewWatch().CreatedAt, DateTimeOffset.Parse(stored, System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static async Task<Snapshot> ReadSnapshotAsync(ArchiveService archive)
        => new(
            Assert.IsType<AnimeArchive>(await archive.GetArchiveAsync(42)),
            await archive.GetAnimeTagsAsync(42),
            await archive.GetWatchHistoryAsync(42),
            await archive.GetScreenshotsAsync(),
            await archive.GetScreenshotSettingsAsync());

    private static void AssertSnapshot(Snapshot expected, Snapshot actual)
    {
        Assert.Equal(expected.Archive, actual.Archive);
        Assert.Equal(expected.Tags, actual.Tags);
        Assert.Equal(expected.History, actual.History);
        Assert.Equal(expected.Screenshots, actual.Screenshots);
        Assert.Equal(expected.Settings, actual.Settings);
    }

    private string LocalPath(string name) => Path.Combine(Path.GetDirectoryName(Factory.DatabasePath)!, name);

    private AnimeScreenshot NewScreenshot()
        => new("new-shot", LocalPath("new.png"), "synthetic stored hash", At, "Window", "process", 640, 360,
            null, null, null, null, "Original context", false);

    private static ManualWatchEvent NewWatch()
        => new("new-watch", 42, "Manual title", At, 2, 4, 72, " new note ", At.AddHours(1));

    private ScreenshotSettings NewSettings() => new(false, LocalPath("new-directory"), true, false);

    private static async Task FinishInvocationAsync(Task<Task> invocation)
    {
        var pending = await invocation.WaitAsync(CompletionBudget);
        try
        {
            await pending.WaitAsync(CompletionBudget);
        }
        catch (SqliteException) when (pending.IsFaulted)
        {
            // 正文断言 SQL 失败；超时仍需暴露，不能把未结束的实际写入当作已排空。
        }
    }

    private sealed record Snapshot(
        AnimeArchive Archive,
        IReadOnlyList<string> Tags,
        IReadOnlyList<WatchHistoryItem> History,
        IReadOnlyList<AnimeScreenshot> Screenshots,
        ScreenshotSettings Settings);
}
