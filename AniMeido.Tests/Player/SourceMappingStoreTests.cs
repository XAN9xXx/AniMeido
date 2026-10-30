using AniMeido.Plugin.Player.Sources;

using System.Reflection;

namespace AniMeido.Tests;

public sealed class SourceMappingStoreTests
{
    private static readonly TimeSpan CompletionBudget = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData("ensure")]
    [InlineData("get")]
    [InlineData("set")]
    [InlineData("remove")]
    [Trait("Category", "Mechanism")]
    public async Task CompleteMappingOperationDoesNotRequireTheCallingUiContext(string action)
    {
        using var paths = new MockAppDataPaths();
        var path = Path.Combine(paths.RootDirectory, "mappings.json");
        await File.WriteAllTextAsync(path, """{"42:fixture":"old-route"}""");
        var store = new SourceMappingStore(path);
        var gate = (SemaphoreSlim)typeof(SourceMappingStore)
            .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        var ui = new PausedUiContext();
        var previousContext = SynchronizationContext.Current;
        Task operation;
        await gate.WaitAsync();
        try
        {
            SynchronizationContext.SetSynchronizationContext(ui);
            operation = action switch
            {
                "ensure" => store.EnsurePublicIdsAsync(default),
                "get" => store.GetAsync(42, "fixture", default),
                "set" => store.SetAsync(42, "fixture", "new-route", default),
                "remove" => store.RemoveAsync(42, "fixture", default),
                _ => throw new ArgumentOutOfRangeException(nameof(action)),
            };
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            gate.Release();
        }

        try
        {
            // 直接观察 UI 依赖，文件操作的完成预算允许 CI 调度和磁盘耗时波动。
            var completed = await Task.WhenAny(operation, ui.FirstPost).WaitAsync(CompletionBudget);
            Assert.Same(operation, completed);
            await operation;
            Assert.Equal(0, ui.PostCount);
        }
        finally
        {
            ui.Resume();
            await Task.WhenAny(operation).WaitAsync(CompletionBudget);
            await operation.ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }

        var expected = action switch { "set" => "new-route", "remove" => null, _ => "old-route" };
        Assert.Equal(expected, await store.GetAsync(42, "fixture", default));
        Assert.Equal(expected, await new SourceMappingStore(path).GetAsync(42, "fixture", default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedFileReplacementKeepsTheLastCommittedMappingAndCanRetry(bool remove)
    {
        using var paths = new MockAppDataPaths();
        var path = Path.Combine(paths.RootDirectory, "mappings.json");
        const string original = """{"42:fixture":"old-route"}""";
        await File.WriteAllTextAsync(path, original);
        var store = new SourceMappingStore(path);
        Assert.Equal("old-route", await store.GetAsync(42, "fixture", default));

        using (var held = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() => remove
                ? store.RemoveAsync(42, "fixture", default)
                : store.SetAsync(42, "fixture", "new-route", default));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Equal("old-route", await store.GetAsync(42, "fixture", default));
            Assert.Equal(original, await File.ReadAllTextAsync(path));
        }

        if (remove)
            await store.RemoveAsync(42, "fixture", default);
        else
            await store.SetAsync(42, "fixture", "new-route", default);

        var expected = remove ? null : "new-route";
        Assert.Equal(expected, await store.GetAsync(42, "fixture", default));
        Assert.Equal(expected, await new SourceMappingStore(path).GetAsync(42, "fixture", default));
        Assert.Empty(Directory.EnumerateFiles(paths.RootDirectory, "*.tmp"));
    }
}
