using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class PersonalDataShutdownTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Stop_WhenIdle_IsPermanentAndReusesCompletion()
    {
        using var paths = new MockAppDataPaths();
        var factory = new SqliteConnectionFactory(paths);

        var stopping = factory.StopAsync();
        Assert.Same(stopping, factory.StopAsync());
        await stopping.WaitAsync(Budget);

        Assert.Throws<ObjectDisposedException>(() => factory.Access.BeginWrite());
        Assert.Throws<ObjectDisposedException>(() => factory.Access.BeginExclusive());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => factory.OpenAsync());
    }

    [Fact]
    public async Task Stop_WaitsForAcceptedOperationAndItsNestedWorkAndConnections()
    {
        using var paths = new MockAppDataPaths();
        var factory = new SqliteConnectionFactory(paths);
        using var outer = factory.Access.BeginWrite();
        var stopping = factory.StopAsync();
        Assert.False(stopping.IsCompleted);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => OutsideOperation(() =>
        {
            using var rejected = factory.Access.BeginWrite();
            return Task.CompletedTask;
        }));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => OutsideOperation(async () =>
        {
            using var rejected = await factory.OpenAsync();
        }));

        await Task.Yield();
        using var nested = factory.Access.BeginWrite();
        outer.Dispose();
        using var connection = await factory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 42";
        Assert.Equal(42L, await command.ExecuteScalarAsync());
        Assert.False(stopping.IsCompleted);

        nested.Dispose();
        Assert.False(stopping.IsCompleted);
        connection.Dispose();
        await stopping.WaitAsync(Budget);
    }

    [Fact]
    public async Task Stop_WaitsForReaderBeforeAcceptedExclusiveOperationCanFinish()
    {
        using var paths = new MockAppDataPaths();
        var factory = new SqliteConnectionFactory(paths);
        using var reader = await factory.OpenAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var import = OutsideOperation(async () =>
        {
            using var exclusive = factory.Access.BeginExclusive();
            entered.SetResult();
            await exclusive.WaitForDrainAsync(default);
            using var nested = factory.Access.BeginExclusive();
            using var connection = await factory.OpenAsync();
            opened.SetResult();
            await release.Task;
        });

        try
        {
            await entered.Task.WaitAsync(Budget);
            var stopping = factory.StopAsync();
            Assert.False(stopping.IsCompleted);
            reader.Dispose();
            await opened.Task.WaitAsync(Budget);
            Assert.False(stopping.IsCompleted);
            release.SetResult();
            await import.WaitAsync(Budget);
            await stopping.WaitAsync(Budget);
        }
        finally
        {
            reader.Dispose();
            release.TrySetResult();
            await import.WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task Stop_TracksAnExclusiveConnectionUntilItActuallyCloses()
    {
        using var paths = new MockAppDataPaths();
        var factory = new SqliteConnectionFactory(paths);
        using var exclusive = factory.Access.BeginExclusive();
        await exclusive.WaitForDrainAsync(default);
        using var connection = await factory.OpenAsync();

        var stopping = factory.StopAsync();
        exclusive.Dispose();
        Assert.False(stopping.IsCompleted);
        connection.Dispose();
        await stopping.WaitAsync(Budget);
    }

    [Fact]
    public async Task Stop_AfterAnOpenFails_DoesNotWaitForItsReleasedLease()
    {
        using var paths = new MockAppDataPaths();
        var factory = new SqliteConnectionFactory(paths);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factory.OpenAsync(cancellation.Token));

        await factory.StopAsync().WaitAsync(Budget);
    }

    [Fact]
    public async Task Stop_AfterRecoveryFails_StillWaitsForTheAcceptedExclusiveOperation()
    {
        using var paths = new MockAppDataPaths();
        var factory = new SqliteConnectionFactory(paths);
        using var exclusive = factory.Access.BeginExclusive();
        await exclusive.WaitForDrainAsync(default);
        exclusive.MarkRecoveryFailed();

        var stopping = factory.StopAsync();
        Assert.False(stopping.IsCompleted);
        exclusive.Dispose();
        await stopping.WaitAsync(Budget);
    }

    private static Task OutsideOperation(Func<Task> action)
    {
        using (ExecutionContext.SuppressFlow())
            return Task.Run(action);
    }
}
