using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;

namespace AniMeido.Tests;

public sealed partial class BrowseHistorySchedulingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoryWrite_ReturnsWhileSqliteIsLocked_AndCompletesWithoutTheCallingContext(bool clear)
    {
        await _history.RecordAsync(1, "原记录");
        using var blocker = await _database.LockExclusiveAsync();
        var before = DateTime.UtcNow;
        var batch = InvokeHistoryWrites(() =>
            [clear ? _history.ClearAsync() : _history.RecordAsync(9, "新记录")]);
        Task[] writes;
        try
        {
            writes = await batch.Invocation.WaitAsync(ReturnBudget);
            Assert.False(Assert.Single(writes).IsCompleted);
            blocker.Dispose();
            await Task.WhenAll(writes).WaitAsync(CompletionBudget);
        }
        finally
        {
            blocker.Dispose();
            batch.Context.Resume();
            await FinishHistoryWritesAsync(batch.Invocation);
        }

        var rows = await _history.GetHistoryAsync();
        if (clear)
        {
            Assert.Empty(rows);
        }
        else
        {
            var written = Assert.Single(rows, row => row.AnimeId == 9);
            Assert.Equal("新记录", written.Title);
            Assert.Equal(1, written.ViewCount);
            Assert.InRange(written.LastViewed.ToUniversalTime(), before, DateTime.UtcNow);
            Assert.Contains(rows, row => row.AnimeId == 1);
        }
    }

    [Theory]
    [InlineData("titles")]
    [InlineData("record-clear-record")]
    [InlineData("clear-record")]
    public async Task QueuedHistoryWrites_PreserveSubmissionOrderTitleRetentionAndCounts(string scenario)
    {
        await _history.RecordAsync(99, "已有记录");
        using var blocker = await _database.LockExclusiveAsync();
        var batch = InvokeHistoryWrites(() => scenario switch
        {
            "titles" =>
            [
                _history.RecordAsync(1, "第一标题"),
                _history.RecordAsync(1, null),
                _history.RecordAsync(1, "最后标题"),
                _history.RecordAsync(1, null),
            ],
            "record-clear-record" =>
            [
                _history.RecordAsync(1, "先写入"),
                _history.ClearAsync(),
                _history.RecordAsync(2, null),
            ],
            "clear-record" => [_history.ClearAsync(), _history.RecordAsync(2, "清空后")],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        });
        try
        {
            var writes = await batch.Invocation.WaitAsync(ReturnBudget);
            Assert.All(writes, write => Assert.False(write.IsCompleted));
            blocker.Dispose();
            await Task.WhenAll(writes).WaitAsync(CompletionBudget);
        }
        finally
        {
            blocker.Dispose();
            batch.Context.Resume();
            await FinishHistoryWritesAsync(batch.Invocation);
        }

        var rows = await _history.GetHistoryAsync();
        if (scenario == "titles")
        {
            var written = Assert.Single(rows, row => row.AnimeId == 1);
            Assert.Equal("最后标题", written.Title);
            Assert.Equal(4, written.ViewCount);
            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, row => row.AnimeId == 99);
        }
        else
        {
            var written = Assert.Single(rows);
            Assert.Equal(2, written.AnimeId);
            Assert.Equal(scenario == "clear-record" ? "清空后" : null, written.Title);
            Assert.Equal(1, written.ViewCount);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedHistoryWrites_HoldAdmissionUntilEveryWriteFinishesBeforeImportOrStop(bool stop)
    {
        using var blocker = await _database.LockExclusiveAsync();
        var batch = InvokeHistoryWrites(() =>
        [
            _history.RecordAsync(1, "先写入"),
            _history.ClearAsync(),
            _history.RecordAsync(2, "最后写入"),
        ]);
        PersonalDataAccess.Operation? import = null;
        Task? drained = null;
        try
        {
            var writes = await batch.Invocation.WaitAsync(ReturnBudget);
            Assert.All(writes, write => Assert.False(write.IsCompleted));
            if (stop)
            {
                drained = _database.Factory.StopAsync();
            }
            else
            {
                import = _database.Factory.Access.BeginExclusive();
                drained = import.WaitForDrainAsync(CancellationToken.None);
            }
            Assert.False(drained.IsCompleted);

            Task rejectedRecord;
            Task rejectedClear;
            using (ExecutionContext.SuppressFlow())
            {
                rejectedRecord = Task.Run(() => _history.RecordAsync(999, "不得写入"));
                rejectedClear = Task.Run(() => _history.ClearAsync());
            }
            if (stop)
            {
                await Assert.ThrowsAsync<ObjectDisposedException>(() => rejectedRecord.WaitAsync(CompletionBudget));
                await Assert.ThrowsAsync<ObjectDisposedException>(() => rejectedClear.WaitAsync(CompletionBudget));
            }
            else
            {
                await Assert.ThrowsAsync<PersonalDataBusyException>(
                    () => rejectedRecord.WaitAsync(CompletionBudget));
                await Assert.ThrowsAsync<PersonalDataBusyException>(
                    () => rejectedClear.WaitAsync(CompletionBudget));
            }

            blocker.Dispose();
            await Task.WhenAll(writes).WaitAsync(CompletionBudget);
            await drained.WaitAsync(CompletionBudget);
        }
        finally
        {
            blocker.Dispose();
            batch.Context.Resume();
            try
            {
                await FinishHistoryWritesAsync(batch.Invocation);
            }
            finally
            {
                import?.Dispose();
                if (drained is not null)
                    await drained.WaitAsync(CompletionBudget);
            }
        }

        // 停机后普通服务读入口已拒绝新请求，只用本测试库的独立连接核对已提交结果。
        using var verification = _database.Factory.CreateConnection();
        await verification.OpenAsync();
        using var command = verification.CreateCommand();
        command.CommandText = "SELECT AnimeID, TitleSnapshot, ViewCount FROM browse_history";
        using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt32(0));
        Assert.Equal("最后写入", reader.GetString(1));
        Assert.Equal(1, reader.GetInt32(2));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task CancellingQueuedClear_DoesNotReleaseAnotherWriteOrDeleteItsResult()
    {
        using var cancellation = new CancellationTokenSource();
        using var blocker = await _database.LockExclusiveAsync();
        var batch = InvokeHistoryWrites(() =>
        [
            _history.RecordAsync(1, "第一条"),
            _history.ClearAsync(cancellation.Token),
            _history.RecordAsync(2, "后续记录"),
        ]);
        try
        {
            var writes = await batch.Invocation.WaitAsync(ReturnBudget);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writes[1].WaitAsync(CompletionBudget));
            Assert.False(writes[0].IsCompleted);
            Assert.False(writes[2].IsCompleted);
            blocker.Dispose();
            await Task.WhenAll(writes[0], writes[2]).WaitAsync(CompletionBudget);
        }
        finally
        {
            cancellation.Cancel();
            blocker.Dispose();
            batch.Context.Resume();
            await FinishHistoryWritesAsync(batch.Invocation);
        }

        var rows = await _history.GetHistoryAsync();
        Assert.Equal(new[] { 1, 2 }, rows.Select(row => row.AnimeId).Order());
        Assert.All(rows, row => Assert.Equal(1, row.ViewCount));
        Assert.Contains(rows, row => row.Title == "第一条");
        Assert.Contains(rows, row => row.Title == "后续记录");
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task FailedHistoryWrite_ReleasesItsGateAndAllowsTheNextQueuedWriteToCommit()
    {
        using (var connection = await _database.Factory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER reject_fixture_history BEFORE INSERT ON browse_history
                WHEN NEW.AnimeID = 1 BEGIN
                    SELECT RAISE(ABORT, 'fixture history write failure');
                END;
                """;
            await command.ExecuteNonQueryAsync();
        }
        using var blocker = await _database.LockExclusiveAsync();
        var batch = InvokeHistoryWrites(() =>
            [_history.RecordAsync(1, "失败记录"), _history.RecordAsync(2, "成功记录")]);
        try
        {
            var writes = await batch.Invocation.WaitAsync(ReturnBudget);
            Assert.All(writes, write => Assert.False(write.IsCompleted));
            blocker.Dispose();
            var failure = await Assert.ThrowsAsync<SqliteException>(() => writes[0].WaitAsync(CompletionBudget));
            Assert.Equal(19, failure.SqliteErrorCode);
            await writes[1].WaitAsync(CompletionBudget);
        }
        finally
        {
            blocker.Dispose();
            batch.Context.Resume();
            await FinishHistoryWritesAsync(batch.Invocation);
        }

        var row = Assert.Single(await _history.GetHistoryAsync());
        Assert.Equal(2, row.AnimeId);
        Assert.Equal("成功记录", row.Title);
        Assert.Equal(1, row.ViewCount);
        await _database.Factory.StopAsync().WaitAsync(CompletionBudget);
    }

    private static (Task<Task[]> Invocation, PausedUiContext Context) InvokeHistoryWrites(Func<Task[]> submit)
    {
        var context = new PausedUiContext();
        var invocation = SchedulingTestThread.InvokeAsync(() => submit(), context);
        return (invocation, context);
    }

    private static async Task FinishHistoryWritesAsync(Task<Task[]> invocation)
    {
        var writes = await invocation.WaitAsync(CompletionBudget);
        var completion = Task.WhenAll(writes);
        try
        {
            await completion.WaitAsync(CompletionBudget);
        }
#pragma warning disable CA1031 // 测试主体断言具体失败；收尾只等待所有真实写任务，不掩盖夹具超时。
        catch (Exception) when (completion.IsCompleted)
        {
        }
#pragma warning restore CA1031
    }
}
