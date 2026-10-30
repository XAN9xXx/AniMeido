using AniMeido.Plugin.Player.Sources.EasyBangumi;
using System.Reflection;

namespace AniMeido.Tests;

public sealed class EasyPreferenceStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Mechanism")]
    public async Task EntirePreferenceOperationCanFinishWhileTheUiContextIsPaused(bool write)
    {
        using var paths = new MockAppDataPaths();
        var store = new EasyPreferenceStore(paths.RootDirectory);
        await store.WriteAsync("fixture", new Dictionary<string, string> { ["Host"] = "old-host" }, default);
        var gate = GetGate(store);
        var ui = new PausedUiContext();
        var previous = SynchronizationContext.Current;
        Task operation;
        await gate.WaitAsync();
        try
        {
            SynchronizationContext.SetSynchronizationContext(ui);
            operation = write
                ? store.WriteAsync("fixture", new Dictionary<string, string> { ["Host"] = "new-host" }, default)
                : store.ReadAsync("fixture", default);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            gate.Release();
        }

        try
        {
            await operation.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, ui.PostCount);
        }
        finally
        {
            ui.Resume();
            await Task.WhenAny(operation).WaitAsync(TimeSpan.FromSeconds(5));
            await operation.ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }

        var reloaded = await new EasyPreferenceStore(paths.RootDirectory).ReadAsync("fixture", default);
        Assert.Equal(write ? "new-host" : "old-host", reloaded["Host"]);
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task QueuedSavesKeepTheirCallOrderAndCancelledSaveDoesNotReplaceThem()
    {
        using var paths = new MockAppDataPaths();
        var store = new EasyPreferenceStore(paths.RootDirectory);
        var gate = GetGate(store);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Task first = Task.CompletedTask;
        Task second = Task.CompletedTask;
        try
        {
            await gate.WaitAsync();
            try
            {
                first = store.WriteAsync("fixture", new Dictionary<string, string> { ["Host"] = "first" }, default);
                second = store.WriteAsync("fixture", new Dictionary<string, string> { ["Host"] = "second" }, default);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync(
                    "fixture", new Dictionary<string, string> { ["Host"] = "cancelled" }, cancelled.Token)
                    .WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally
            {
                gate.Release();
            }

            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("second", (await new EasyPreferenceStore(paths.RootDirectory).ReadAsync("fixture", default))["Host"]);
            Assert.Empty(await store.ReadAsync("missing", default));
        }
        finally
        {
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static SemaphoreSlim GetGate(EasyPreferenceStore store)
        => (SemaphoreSlim)typeof(EasyPreferenceStore)
            .GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;
}
