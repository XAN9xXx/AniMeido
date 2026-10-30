using AniMeido.App.Services;
using AniMeido.Contracts;
using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Collections;

namespace AniMeido.Tests;

public sealed class PluginShutdownTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAfterDataDrain_WaitsBeforeEnumeratingOrDisposingPlugins(bool holdReader)
    {
        using var paths = new MockAppDataPaths();
        var factory = new SqliteConnectionFactory(paths);
        var operationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = new FakePlugin(async () =>
        {
            disposalEntered.SetResult();
            await finishDisposal.Task;
        });
        var plugins = new ObservedPluginList(plugin);
        var operation = OutsideOperation(async () =>
        {
            using var write = holdReader ? null : factory.Access.BeginWrite();
            using var reader = holdReader ? await factory.OpenAsync() : null;
            operationEntered.SetResult();
            await finishOperation.Task;
        });
        Task? stopping = null;

        try
        {
            await operationEntered.Task.WaitAsync(Budget);

            stopping = PluginStartup.StopAfterDataDrainAsync(plugins, factory);

            Assert.False(stopping.IsCompleted);
            Assert.Equal(0, plugins.EnumerationCount);
            Assert.Equal(0, plugin.DisposeCount);
            Assert.False(disposalEntered.Task.IsCompleted);

            finishOperation.SetResult();
            await operation.WaitAsync(Budget);
            await disposalEntered.Task.WaitAsync(Budget);
            Assert.False(stopping.IsCompleted);
            Assert.Equal(1, plugins.EnumerationCount);
            Assert.Equal(1, plugin.DisposeCount);
            Assert.Throws<ObjectDisposedException>(() => factory.Access.BeginWrite());

            finishDisposal.SetResult();
            await stopping.WaitAsync(Budget);
        }
        finally
        {
            finishOperation.TrySetResult();
            finishDisposal.TrySetResult();
            await operation.WaitAsync(Budget);
            if (stopping is not null)
            {
                await stopping.WaitAsync(Budget);
            }
        }
    }

    [Fact]
    public async Task StopAfterDataDrain_PropagatesThePluginFailureAfterStoppingDataAccess()
    {
        using var paths = new MockAppDataPaths();
        var factory = new SqliteConnectionFactory(paths);
        var failure = new IOException("fixture plugin shutdown failure");
        var plugin = new FakePlugin(() => Task.FromException(failure));

        var observed = await Assert.ThrowsAsync<IOException>(
            () => PluginStartup.StopAfterDataDrainAsync([plugin], factory).WaitAsync(Budget));

        Assert.Same(failure, observed);
        Assert.Equal(1, plugin.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => factory.Access.BeginWrite());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => factory.OpenAsync());
    }

    private static Task OutsideOperation(Func<Task> action)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(action);
        }
    }

    private sealed class FakePlugin(Func<Task> dispose) : IPlugin, IAsyncDisposable
    {
        public string PluginID => "fixture.shutdown";
        public string DisplayName => "Fixture";
        public string Version => "1.0.0";
        public bool IsRequired => true;
        public int DisposeCount { get; private set; }

        public Task InitializeAsync(IServiceCollection services) => Task.CompletedTask;
        public IEnumerable<PluginNavigationItem> GetNavigationItems() => [];

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return new ValueTask(dispose());
        }
    }

    private sealed class ObservedPluginList(IPlugin plugin) : IReadOnlyList<IPlugin>
    {
        public int EnumerationCount { get; private set; }
        public int Count => 1;
        public IPlugin this[int index] => index == 0 ? plugin : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<IPlugin> GetEnumerator()
        {
            EnumerationCount++;
            return ((IEnumerable<IPlugin>)new[] { plugin }).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
