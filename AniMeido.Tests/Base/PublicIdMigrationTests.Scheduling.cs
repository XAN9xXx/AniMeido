using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed partial class PublicIdMigrationTests
{
    private static readonly TimeSpan SchedulingReturnBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SchedulingCompletionBudget = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Migration_ReturnsWhileSqliteIsLocked_AndFinishesWithoutTheCallingUiContext()
    {
        await CreateV8Async();
        await ExecuteAsync("INSERT INTO tracking VALUES(1,1,@at)");
        using var handler = new MappingHandler();
        using var client = Client(handler);
        using var blocker = await LockMigrationDatabaseAsync();
        var context = new PausedUiContext();
        var invocation = InvokeMigrationFromPausedUi(Service(client), context);
        try
        {
            var migrating = await invocation.Returned.WaitAsync(SchedulingReturnBudget);
            Assert.False(migrating.IsCompleted);
            Assert.Empty(handler.Paths);

            blocker.Dispose();
            await migrating.WaitAsync(SchedulingCompletionBudget);

            Assert.Equal(0, context.PostCount);
            await AssertSchedulingMigrationResultAsync();
        }
        finally
        {
            blocker.Dispose();
            context.Resume();
            await FinishMigrationInvocationAsync(invocation);
        }
    }

    [Fact]
    public async Task Migration_KeepsAdmissionThroughStopAndThePostCommitMapConnection()
    {
        await CreateV8Async();
        await ExecuteAsync("INSERT INTO tracking VALUES(1,1,@at)");
        using var handler = new GatedMigrationHandler(new MappingHandler());
        using var client = Client(handler);
        var migrating = Service(client).MigrateAsync();
        Task? stopping = null;
        try
        {
            await handler.Entered.WaitAsync(SchedulingCompletionBudget);
            stopping = DbFactory.StopAsync();
            Assert.False(migrating.IsCompleted);
            Assert.False(stopping.IsCompleted);

            handler.Release();
            await migrating.WaitAsync(SchedulingCompletionBudget);
            await stopping.WaitAsync(SchedulingCompletionBudget);

            // 工厂已停止；仅用本夹具的独立只读连接核对提交与 map 发布。
            await AssertSchedulingMigrationResultAsync();
        }
        finally
        {
            handler.Release();
            await Task.WhenAny(migrating).WaitAsync(SchedulingCompletionBudget);
            await migrating.ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            if (stopping is not null)
            {
                await stopping.WaitAsync(SchedulingCompletionBudget);
            }
        }
    }

    private async Task<SqliteConnection> LockMigrationDatabaseAsync()
    {
        using (var pooled = DbFactory.CreateConnection())
        {
            SqliteConnection.ClearPool(pooled);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        try
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandTimeout = 2;
            // WAL 允许读取绕过写锁；临时库改用文件独占锁，直到测试主动放行。
            command.CommandText = "PRAGMA journal_mode=DELETE";
            Assert.Equal("delete", await command.ExecuteScalarAsync());
            command.CommandText = "BEGIN EXCLUSIVE";
            await command.ExecuteNonQueryAsync();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private async Task AssertSchedulingMigrationResultAsync()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        Assert.Equal(PersonalDatabaseSchema.CurrentVersion, Convert.ToInt32(await command.ExecuteScalarAsync()));
        command.CommandText = "SELECT AnimeId FROM tracking";
        Assert.Equal(101, Convert.ToInt32(await command.ExecuteScalarAsync()));

        var path = Path.Combine(Paths.RootDirectory, "id-migration-v9.json");
        using var map = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(101, map.RootElement.GetProperty("workIds").GetProperty("1").GetInt32());
    }

    private static MigrationInvocation InvokeMigrationFromPausedUi(
        PublicIdMigrationService service, PausedUiContext context)
    {
        var returned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                returned.SetResult(service.MigrateAsync());
            }
#pragma warning disable CA1031 // 专用调用线程把同步失败交还测试，不让未观察异常退出进程。
            catch (Exception error)
            {
                returned.SetException(error);
            }
#pragma warning restore CA1031
            finally
            {
                SynchronizationContext.SetSynchronizationContext(null);
                exited.SetResult();
            }
        }) { IsBackground = true };
        thread.Start();
        return new MigrationInvocation(returned.Task, exited.Task);
    }

    private static async Task FinishMigrationInvocationAsync(MigrationInvocation invocation)
    {
        try
        {
            var migrating = await invocation.Returned.WaitAsync(SchedulingCompletionBudget);
            await Task.WhenAny(migrating).WaitAsync(SchedulingCompletionBudget);
            await migrating.ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
        finally
        {
            await invocation.Exited.WaitAsync(SchedulingCompletionBudget);
        }
    }

    private sealed record MigrationInvocation(Task<Task> Returned, Task Exited);

    private sealed class GatedMigrationHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requests;

        internal Task Entered => _entered.Task;

        internal void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requests) == 1)
            {
                _entered.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
