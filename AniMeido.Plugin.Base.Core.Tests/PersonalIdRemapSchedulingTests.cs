using System.Text.Json;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMeido.Tests;

public sealed class PersonalIdRemapSchedulingTests : IAsyncLifetime
{
    private static readonly TimeSpan ReturnBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CompletionBudget = TimeSpan.FromSeconds(5);
    private readonly PersonalDatabaseFixture _database = new();
    private SqliteConnectionFactory Factory => _database.Factory;
    private string LogPath => Path.Combine(Path.GetDirectoryName(Factory.DatabasePath)!, "id-remap-log.json");

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        await new TrackingService(Factory).SetStatusAsync(-1, AnimeTrackingStatus.Following);
        using var connection = await Factory.OpenAsync();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO legacy_anime_refs(LocalId,LegacyBangumiId,Reason,TitleSnapshot,RecordedAt,KeptAt)
                VALUES(-1,1,'not_found','保留作品','2026-10-20',NULL)
                """;
            await command.ExecuteNonQueryAsync();
        }
        // 固定历史尚未发布，供实际 Flush/提交入口处理。
        using var log = connection.CreateCommand();
        log.CommandText = "INSERT INTO config(Key,Value) VALUES(@key,@value)";
        log.Parameters.AddWithValue("@key", PersonalIdRemapService.LogConfigKey);
        log.Parameters.AddWithValue("@value", """[{"seq":1,"from":7,"to":8,"at":"2026-10-20T00:00:00Z"}]""");
        await log.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => _database.DisposeAsync();

    [Theory]
    [InlineData("rebind")]
    [InlineData("keep")]
    [InlineData("flush")]
    public async Task IdentityOperation_ReturnsWhileSqliteIsLocked_AndFinishesWithoutTheCallingContext(string operation)
    {
        var remap = CreateRemap();
        var changed = 0;
        var publishedBeforeNotification = false;
        remap.Changed += () =>
        {
            publishedBeforeNotification = File.Exists(LogPath);
            Interlocked.Increment(ref changed);
        };
        using var blocker = await _database.LockExclusiveAsync();
        var context = new PausedUiContext();
        var invocation = SchedulingTestThread.InvokeAsync<Task>(() => RunAsync(remap, operation), context);
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

        using var log = JsonDocument.Parse(await File.ReadAllTextAsync(LogPath));
        Assert.Equal(operation == "rebind" ? 2 : 1, log.RootElement.GetArrayLength());
        var first = log.RootElement[0];
        Assert.Equal(1, first.GetProperty("seq").GetInt64());
        Assert.Equal(7, first.GetProperty("from").GetInt32());
        Assert.Equal(8, first.GetProperty("to").GetInt32());
        Assert.Equal(operation == "flush" ? 0 : 1, changed);
        if (operation != "flush")
            Assert.True(publishedBeforeNotification);
        var statuses = (await new TrackingService(Factory).GetAllTrackingAsync())
            .ToDictionary(row => row.AnimeId, row => row.Status);
        if (operation == "rebind")
        {
            Assert.Equal(AnimeTrackingStatus.Following, statuses[2]);
            Assert.False(statuses.ContainsKey(-1));
            var rewritten = log.RootElement[1];
            Assert.Equal(-1, rewritten.GetProperty("from").GetInt32());
            Assert.Equal(2, rewritten.GetProperty("to").GetInt32());
            Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM legacy_anime_refs"));
        }
        else
        {
            Assert.Equal(AnimeTrackingStatus.Following, statuses[-1]);
            var kept = await ScalarAsync("SELECT KeptAt FROM legacy_anime_refs WHERE LocalId=-1");
            if (operation == "keep")
                Assert.IsType<string>(kept);
            else
                Assert.Equal(DBNull.Value, kept);
        }
    }

    [Theory]
    [InlineData("rebind")]
    [InlineData("keep")]
    [InlineData("flush")]
    public async Task IdentityOperation_PreCancelledRequestReleasesAdmissionAndDoesNotPublish(string operation)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var remap = CreateRemap();
        var changed = 0;
        remap.Changed += () => Interlocked.Increment(ref changed);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(remap, operation, cancellation.Token));

        Assert.Equal(0, changed);
        Assert.False(File.Exists(LogPath));
        await Factory.StopAsync().WaitAsync(CompletionBudget);
    }

    [Fact]
    public async Task FailedRebind_RollsBackAndReleasesTheGateForTheNextRealOperation()
    {
        var remap = CreateRemap();
        var changed = 0;
        remap.Changed += () => Interlocked.Increment(ref changed);

        await Assert.ThrowsAsync<InvalidOperationException>(() => remap.RebindAsync(-1, -2, false));
        Assert.Equal(0, changed);
        Assert.False(File.Exists(LogPath));
        Assert.Equal(AnimeTrackingStatus.Following,
            Assert.Single(await new TrackingService(Factory).GetAllTrackingAsync(), row => row.AnimeId == -1).Status);

        await new PendingReviewService(Factory, remap).SetKeptAsync(-1, true).WaitAsync(CompletionBudget);
        await remap.RebindAsync(-1, 2, false).WaitAsync(CompletionBudget);

        Assert.Equal(2, changed);
        Assert.Equal(AnimeTrackingStatus.Following,
            Assert.Single(await new TrackingService(Factory).GetAllTrackingAsync(), row => row.AnimeId == 2).Status);
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM legacy_anime_refs"));
    }

    [Fact]
    public async Task FlushLog_PublicationFailureReleasesTheGateAndTheNextFlushPublishesTheSameCommittedLog()
    {
        var remap = CreateRemap();
        var changed = 0;
        remap.Changed += () => Interlocked.Increment(ref changed);
        Directory.CreateDirectory(LogPath);
        try
        {
            await remap.FlushLogAsync().WaitAsync(CompletionBudget);
            Assert.True(Directory.Exists(LogPath));
            Assert.Equal(0, changed);
        }
        finally
        {
            Directory.Delete(LogPath);
        }

        await remap.FlushLogAsync().WaitAsync(CompletionBudget);

        using var log = JsonDocument.Parse(await File.ReadAllTextAsync(LogPath));
        Assert.Equal(1, log.RootElement.GetArrayLength());
        Assert.Equal(7, log.RootElement[0].GetProperty("from").GetInt32());
        Assert.Equal(8, log.RootElement[0].GetProperty("to").GetInt32());
        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task CancellationAfterCommit_DoesNotSkipLogPublicationOrFinalNotification()
    {
        using var cancellation = new CancellationTokenSource();
        var cache = new CacheService(Factory);
        var remap = new PersonalIdRemapService(Factory, new CancelOnCacheFailureLogger(cancellation), cache);
        var notified = false;
        remap.Changed += () => notified = cancellation.IsCancellationRequested && File.Exists(LogPath);

        await remap.CommitIdentityChangesAsync(async (connection, transaction, log) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DROP TABLE cache";
            await command.ExecuteNonQueryAsync(cancellation.Token);
            await log.AppendAsync(-1, 2, cancellation.Token);
        }, cancellation.Token, invalidateCache: true).WaitAsync(CompletionBudget);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(notified);
        using var log = JsonDocument.Parse(await File.ReadAllTextAsync(LogPath));
        Assert.Equal(2, log.RootElement.GetArrayLength());
        Assert.Equal(-1, log.RootElement[1].GetProperty("from").GetInt32());
        Assert.Equal(2, log.RootElement[1].GetProperty("to").GetInt32());
    }

    private PersonalIdRemapService CreateRemap()
        => new(Factory, NullLogger<PersonalIdRemapService>.Instance);

    private Task RunAsync(PersonalIdRemapService remap, string operation, CancellationToken ct = default)
        => operation switch
        {
            "rebind" => remap.RebindAsync(-1, 2, false, ct),
            "keep" => new PendingReviewService(Factory, remap).SetKeptAsync(-1, true, ct),
            "flush" => remap.FlushLogAsync(ct),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private static async Task FinishInvocationAsync(Task<Task> invocation)
    {
        var pending = await invocation.WaitAsync(CompletionBudget);
        try
        {
            await pending.WaitAsync(CompletionBudget);
        }
        catch (SqliteException) when (pending.IsFaulted)
        {
            // SQL 故障由正文报告；这里只排空实际任务，等待超时继续抛出。
        }
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        using var connection = await Factory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private sealed class CancelOnCacheFailureLogger(CancellationTokenSource cancellation) : ILogger<PersonalIdRemapService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning && exception is SqliteException)
                cancellation.Cancel();
        }
    }
}
