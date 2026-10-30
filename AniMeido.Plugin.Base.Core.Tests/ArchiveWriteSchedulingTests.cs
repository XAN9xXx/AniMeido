using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;

namespace AniMeido.Tests;

public sealed class ArchiveWriteSchedulingTests : IAsyncLifetime
{
    private static readonly TimeSpan ReturnBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CompletionBudget = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset OccurredAt = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly PersonalDatabaseFixture _database = new();
    private SqliteConnectionFactory Factory => _database.Factory;

    public Task InitializeAsync() => _database.InitializeAsync();

    public Task DisposeAsync() => _database.DisposeAsync();

    public enum WriteKind
    {
        UpsertArchive,
        AddEntry,
        UpdateEntry,
        DeleteEntry,
        SaveArchiveWithTags,
    }

    [Theory]
    [InlineData(WriteKind.UpsertArchive)]
    [InlineData(WriteKind.SaveArchiveWithTags)]
    [InlineData(WriteKind.AddEntry)]
    [InlineData(WriteKind.UpdateEntry)]
    [InlineData(WriteKind.DeleteEntry)]
    [Trait("Category", "Mechanism")]
    public async Task Write_ReturnsWhileSqliteIsLocked_AndKeepsAdmissionUntilItReallyFinishes(WriteKind kind)
    {
        var (archive, parent, entry) = await SeedAsync();
        if (kind == WriteKind.SaveArchiveWithTags)
            await archive.SetAnimeTagsAsync(42, ["原标签"]);
        using var blocker = await _database.LockExclusiveAsync();
        var context = new PausedUiContext();
        var invocation = SchedulingTestThread.InvokeAsync<Task>(() => RunAsync(archive, kind, entry), context);
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

        await AssertSavedAsync(archive, kind, parent, entry);
    }

    [Theory]
    [InlineData(WriteKind.UpsertArchive)]
    [InlineData(WriteKind.SaveArchiveWithTags)]
    [InlineData(WriteKind.AddEntry)]
    [InlineData(WriteKind.UpdateEntry)]
    [InlineData(WriteKind.DeleteEntry)]
    public async Task Write_PreCancelledRequestKeepsExistingRecordsAndReleasesAdmission(WriteKind kind)
    {
        var (archive, parent, entry) = await SeedAsync();
        if (kind == WriteKind.SaveArchiveWithTags)
            await archive.SetAnimeTagsAsync(42, ["原标签"]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(archive, kind, entry, cancellation.Token));

        Assert.Equal(parent, await archive.GetArchiveAsync(42));
        Assert.Equal(entry, Assert.Single(await archive.GetEntriesAsync(42)));
        if (kind == WriteKind.SaveArchiveWithTags)
            Assert.Equal(new[] { "原标签" }, await archive.GetAnimeTagsAsync(42));
        await Factory.StopAsync().WaitAsync(CompletionBudget);
    }

    [Theory]
    [InlineData(WriteKind.UpsertArchive)]
    [InlineData(WriteKind.SaveArchiveWithTags)]
    [InlineData(WriteKind.AddEntry)]
    [InlineData(WriteKind.UpdateEntry)]
    [InlineData(WriteKind.DeleteEntry)]
    public async Task Write_SqlFailurePreservesRecordsAndAllowsTheNextRealWrite(WriteKind kind)
    {
        var (archive, parent, entry) = await SeedAsync();
        if (kind == WriteKind.SaveArchiveWithTags)
            await archive.SetAnimeTagsAsync(42, ["原标签"]);
        var (table, action) = kind switch
        {
            WriteKind.UpsertArchive or WriteKind.SaveArchiveWithTags => ("anime_archives", "UPDATE"),
            WriteKind.AddEntry => ("archive_entries", "INSERT"),
            WriteKind.UpdateEntry => ("archive_entries", "UPDATE"),
            WriteKind.DeleteEntry => ("archive_entries", "DELETE"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        using (var connection = await Factory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                CREATE TRIGGER fail_archive_write BEFORE {action} ON {table}
                BEGIN SELECT RAISE(ABORT, 'synthetic archive write failure'); END
                """;
            await command.ExecuteNonQueryAsync();
        }

        var error = await Record.ExceptionAsync(() => RunAsync(archive, kind, entry));
        Assert.IsType<SqliteException>(error);
        Assert.Equal(parent, await archive.GetArchiveAsync(42));
        Assert.Equal(entry, Assert.Single(await archive.GetEntriesAsync(42)));
        if (kind == WriteKind.SaveArchiveWithTags)
            Assert.Equal(new[] { "原标签" }, await archive.GetAnimeTagsAsync(42));
        using (var connection = await Factory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TRIGGER fail_archive_write";
            await command.ExecuteNonQueryAsync();
        }

        await RunAsync(archive, kind, entry).WaitAsync(CompletionBudget);
        await AssertSavedAsync(archive, kind, parent, entry);
        await Factory.StopAsync().WaitAsync(CompletionBudget);
    }

    private static Task RunAsync(
        ArchiveService archive,
        WriteKind kind,
        ArchiveEntry entry,
        CancellationToken ct = default)
        => kind switch
        {
            WriteKind.UpsertArchive => archive.UpsertArchiveAsync(42, " 修改作品 ", 9, " 修改摘要 ", ct),
            WriteKind.SaveArchiveWithTags => archive.SaveArchiveWithTagsAsync(
                42, " 修改作品 ", 9, " 修改摘要 ", [" Alpha ", "alpha", "", " Beta "], ct),
            WriteKind.AddEntry => archive.AddEntryAsync(42, OccurredAt.AddDays(1), 4, " 新增感想 ", ct),
            WriteKind.UpdateEntry => archive.UpdateEntryAsync(entry.EntryId, OccurredAt.AddDays(1), 4, " 修改感想 ", ct),
            WriteKind.DeleteEntry => archive.DeleteEntryAsync(entry.EntryId, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private async Task<(ArchiveService Archive, AnimeArchive Parent, ArchiveEntry Entry)> SeedAsync()
    {
        var archive = new ArchiveService(Factory);
        await archive.UpsertArchiveAsync(42, "原作品", 8.5, "原摘要");
        await archive.AddEntryAsync(42, OccurredAt, 3, "原感想");
        return (archive, Assert.IsType<AnimeArchive>(await archive.GetArchiveAsync(42)),
            Assert.Single(await archive.GetEntriesAsync(42)));
    }

    private static async Task AssertSavedAsync(
        ArchiveService archive,
        WriteKind kind,
        AnimeArchive originalParent,
        ArchiveEntry originalEntry)
    {
        var parent = Assert.IsType<AnimeArchive>(await archive.GetArchiveAsync(42));
        var entries = await archive.GetEntriesAsync(42);
        if (kind is WriteKind.UpsertArchive or WriteKind.SaveArchiveWithTags)
        {
            Assert.Equal(originalParent with
            {
                TitleSnapshot = "修改作品",
                PersonalRating = 9,
                SummaryNote = "修改摘要",
                UpdatedAt = parent.UpdatedAt,
            }, parent);
            Assert.Equal(originalEntry, Assert.Single(entries));
            if (kind == WriteKind.SaveArchiveWithTags)
                Assert.Equal(new[] { "Alpha", "Beta" }, await archive.GetAnimeTagsAsync(42));
            return;
        }

        Assert.Equal(originalParent, parent);
        switch (kind)
        {
            case WriteKind.AddEntry:
                Assert.Equal(2, entries.Count);
                Assert.Equal(originalEntry, entries.Single(item => item.EntryId == originalEntry.EntryId));
                var added = entries.Single(item => item.EntryId != originalEntry.EntryId);
                Assert.Equal(42, added.AnimeId);
                Assert.Equal(OccurredAt.AddDays(1), added.OccurredAt);
                Assert.Equal(4, added.EpisodeNumber);
                Assert.Equal("新增感想", added.Body);
                Assert.False(string.IsNullOrWhiteSpace(added.EntryId));
                Assert.Equal(added.CreatedAt, added.UpdatedAt);
                break;
            case WriteKind.UpdateEntry:
                var updated = Assert.Single(entries);
                Assert.Equal(originalEntry with
                {
                    OccurredAt = OccurredAt.AddDays(1),
                    EpisodeNumber = 4,
                    Body = "修改感想",
                    UpdatedAt = updated.UpdatedAt,
                }, updated);
                break;
            case WriteKind.DeleteEntry:
                Assert.Empty(entries);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
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
            // 正文报告 SQL 失败；收尾仍等待真实任务，超时不能被当作已结束。
        }
    }
}
