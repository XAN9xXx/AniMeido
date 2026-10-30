using AniMeido.Plugin.Player.Sources.Web;
using System.Reflection;

namespace AniMeido.Tests;

[Trait("Category", "Mechanism")]
public sealed class PlayerRuntimeSettingsSchedulingTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteSettingsOperationCanFinishWhileTheCallingUiContextIsPaused(bool write)
    {
        using var paths = new MockAppDataPaths();
        var path = Path.Combine(paths.RootDirectory, "runtime.json");
        var store = new PlayerRuntimeSettingsStore(path);
        await store.WriteAsync(Settings(40), CancellationToken.None);
        var gate = GetGate(store);
        var ui = new PausedUiContext();
        var previous = SynchronizationContext.Current;
        Task operation;
        await gate.WaitAsync();
        try
        {
            SynchronizationContext.SetSynchronizationContext(ui);
            operation = write
                ? store.WriteAsync(Settings(65), CancellationToken.None)
                : store.ReadAsync(CancellationToken.None);
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
            await Task.WhenAny(operation).WaitAsync(Budget);
            await operation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }

        var saved = await new PlayerRuntimeSettingsStore(path).ReadAsync(CancellationToken.None);
        Assert.Null(saved.FallbackReason);
        Assert.Equal(write ? 65 : 40, saved.Settings.GlobalTimeoutSeconds);
        Assert.Equal(70, saved.Settings.SourceTimeoutSeconds["source-a"]);
    }

    [Fact]
    public void BothEntrypointsQueueFirstAndRunTheirCompleteFileOperationOnAWorker()
    {
        var source = File.ReadAllText(Path.Combine(AppDataRootsTests.RepositoryRoot,
            "Plugins", "AniMeido.Plugin.Player", "Sources", "Web", "PlayerRuntimeSettingsStore.cs"));
        var read = MethodBody(source, "public async Task<PlayerRuntimeReadResult> ReadAsync(");
        var write = MethodBody(source, "public async Task WriteAsync(");

        AssertQueuedWorker(read, "ReadCoreAsync");
        AssertQueuedWorker(write, "WriteCoreAsync");
        Assert.Contains("allowReadFallback: true", read, StringComparison.Ordinal);
        Assert.DoesNotContain("File.", read, StringComparison.Ordinal);
        Assert.DoesNotContain("File.", write, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.", write, StringComparison.Ordinal);

        var writeCore = MethodBody(source, "private async Task WriteCoreAsync(");
        var strictRead = writeCore.IndexOf("allowReadFallback: false", StringComparison.Ordinal);
        var directory = writeCore.IndexOf("Directory.CreateDirectory(", StringComparison.Ordinal);
        var temporaryWrite = writeCore.IndexOf("File.WriteAllTextAsync(", StringComparison.Ordinal);
        var replace = writeCore.IndexOf("File.Move(", StringComparison.Ordinal);
        Assert.True(strictRead >= 0 && directory > strictRead);
        Assert.True(temporaryWrite > directory && replace > temporaryWrite);
    }

    [Fact]
    public async Task QueuedWritesKeepTheirCallOrderAndCancelledWriteDoesNotReplaceThem()
    {
        using var paths = new MockAppDataPaths();
        var path = Path.Combine(paths.RootDirectory, "runtime.json");
        var store = new PlayerRuntimeSettingsStore(path);
        var gate = GetGate(store);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Task first = Task.CompletedTask;
        Task second = Task.CompletedTask;
        try
        {
            await gate.WaitAsync();
            try
            {
                first = store.WriteAsync(Settings(40), CancellationToken.None);
                second = store.WriteAsync(Settings(65), CancellationToken.None);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    store.WriteAsync(Settings(90), cancellation.Token).WaitAsync(Budget));
            }
            finally
            {
                gate.Release();
            }

            await Task.WhenAll(first, second).WaitAsync(Budget);
            var saved = await new PlayerRuntimeSettingsStore(path).ReadAsync(CancellationToken.None);
            Assert.Null(saved.FallbackReason);
            Assert.Equal(65, saved.Settings.GlobalTimeoutSeconds);
            Assert.Equal(70, saved.Settings.SourceTimeoutSeconds["source-a"]);
            Assert.Equal([path], Directory.GetFiles(paths.RootDirectory));
        }
        finally
        {
            await Task.WhenAll(first, second).WaitAsync(Budget);
        }
    }

    private static void AssertQueuedWorker(string body, string coreMethod)
    {
        var queued = body.IndexOf("_gate.WaitAsync(", StringComparison.Ordinal);
        var worker = body.IndexOf("Task.Run(", StringComparison.Ordinal);
        var released = body.IndexOf("_gate.Release()", StringComparison.Ordinal);
        Assert.True(queued >= 0 && worker > queued && released > worker);
        Assert.Contains(".ConfigureAwait(false)", body[queued..worker], StringComparison.Ordinal);
        Assert.Matches(@"Task\.Run\(\s*\(\)\s*=>\s*" + coreMethod + @"\(", body);
    }

    private static string MethodBody(string source, string declaration)
    {
        var start = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Declaration not found: {declaration}");
        var opening = source.IndexOf('{', start);
        Assert.True(opening > start);
        var depth = 0;
        for (var position = opening; position < source.Length; position++)
        {
            if (source[position] == '{')
                depth++;
            else if (source[position] == '}' && --depth == 0)
                return source[(opening + 1)..position];
        }

        throw new InvalidOperationException($"Method body not found: {declaration}");
    }

    private static PlayerRuntimeSettings Settings(int globalTimeout)
    {
        var settings = PlayerRuntimeSettings.CreateDefault();
        settings.GlobalTimeoutSeconds = globalTimeout;
        settings.SourceTimeoutSeconds["source-a"] = 70;
        return settings;
    }

    private static SemaphoreSlim GetGate(PlayerRuntimeSettingsStore store)
        => (SemaphoreSlim)typeof(PlayerRuntimeSettingsStore)
            .GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;
}
