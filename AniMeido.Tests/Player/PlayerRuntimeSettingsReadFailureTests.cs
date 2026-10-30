using AniMeido.Plugin.Player.Sources.Web;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class PlayerRuntimeSettingsReadFailureTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData("{broken-json")]
    [InlineData("null")]
    [InlineData("{\"SchemaVersion\":2}")]
    [InlineData("{\"GlobalTimeoutSeconds\":9}")]
    [InlineData("{\"SourceTimeoutSeconds\":{\"source-a\":121}}")]
    [InlineData("{\"SourceTimeoutSeconds\":null}")]
    public async Task ReadFallbackDoesNotAuthorizeSavingOverInvalidSettings(string original)
    {
        using var paths = new MockAppDataPaths();
        var path = Path.Combine(paths.RootDirectory, "runtime.json");
        await File.WriteAllTextAsync(path, original);
        var store = new PlayerRuntimeSettingsStore(path);

        var read = await store.ReadAsync(CancellationToken.None).WaitAsync(Budget);
        AssertDefaultSettings(read.Settings);
        Assert.NotNull(read.FallbackReason);
        var settings = read.Settings;
        settings.GlobalTimeoutSeconds = 45;

        var error = await Record.ExceptionAsync(() =>
            store.WriteAsync(settings, CancellationToken.None).WaitAsync(Budget));

        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.NotNull(error);
        Assert.False(error is OperationCanceledException);
        Assert.Equal([path], Directory.GetFiles(paths.RootDirectory));
    }

    [Fact]
    public async Task ReadIoFallbackPreservesTheOriginalAndAllowsSavingAfterTheHandleIsReleased()
    {
        using var paths = new MockAppDataPaths();
        var path = Path.Combine(paths.RootDirectory, "runtime.json");
        var original = JsonSerializer.Serialize(ExistingSettings());
        await File.WriteAllTextAsync(path, original);
        var store = new PlayerRuntimeSettingsStore(path);
        Exception? error;

        // 拒绝读取、允许替换，区分严格读取失败和最终替换失败。
        using (var unreadable = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
            FileShare.Write | FileShare.Delete))
        {
            var read = await store.ReadAsync(CancellationToken.None).WaitAsync(Budget);
            AssertDefaultSettings(read.Settings);
            Assert.True(read.FallbackReason is IOException or UnauthorizedAccessException);
            var settings = read.Settings;
            settings.GlobalTimeoutSeconds = 45;
            error = await Record.ExceptionAsync(() =>
                store.WriteAsync(settings, CancellationToken.None).WaitAsync(Budget));
        }

        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.True(error is IOException or UnauthorizedAccessException);

        var replacement = ExistingSettings();
        replacement.GlobalTimeoutSeconds = 45;
        await store.WriteAsync(replacement, CancellationToken.None).WaitAsync(Budget);
        var saved = await store.ReadAsync(CancellationToken.None).WaitAsync(Budget);
        Assert.Null(saved.FallbackReason);
        Assert.Equal(45, saved.Settings.GlobalTimeoutSeconds);
        Assert.Equal(70, saved.Settings.SourceTimeoutSeconds["source-a"]);
    }

    [Fact]
    public async Task MissingSettingsAllowTheFirstSave()
    {
        using var paths = new MockAppDataPaths();
        var path = Path.Combine(paths.RootDirectory, "Settings", "runtime.json");
        var store = new PlayerRuntimeSettingsStore(path);

        var read = await store.ReadAsync(CancellationToken.None).WaitAsync(Budget);
        AssertDefaultSettings(read.Settings);
        Assert.Null(read.FallbackReason);
        Assert.False(File.Exists(path));
        await store.WriteAsync(ExistingSettings(), CancellationToken.None).WaitAsync(Budget);

        var saved = await store.ReadAsync(CancellationToken.None).WaitAsync(Budget);
        Assert.Null(saved.FallbackReason);
        Assert.Equal(40, saved.Settings.GlobalTimeoutSeconds);
        Assert.Equal(70, saved.Settings.SourceTimeoutSeconds["source-a"]);
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task DirectoryAtTheSettingsPathReportsReadFailureAndRemainsUntouched()
    {
        using var paths = new MockAppDataPaths();
        var path = Path.Combine(paths.RootDirectory, "runtime.json");
        Directory.CreateDirectory(path);
        var store = new PlayerRuntimeSettingsStore(path);

        var read = await store.ReadAsync(CancellationToken.None).WaitAsync(Budget);
        AssertDefaultSettings(read.Settings);
        Assert.True(read.FallbackReason is IOException or UnauthorizedAccessException);
        var error = await Record.ExceptionAsync(() =>
            store.WriteAsync(ExistingSettings(), CancellationToken.None).WaitAsync(Budget));

        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.True(Directory.Exists(path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(path));
        Assert.Empty(Directory.GetFiles(paths.RootDirectory));
    }

    [Fact]
    public async Task EffectiveTimeoutCarriesTheReadFailureAlongsideTheUsualDefaultAndSourceRules()
    {
        using var paths = new MockAppDataPaths();
        var path = Path.Combine(paths.RootDirectory, "runtime.json");
        const string Original = "{broken-json";
        await File.WriteAllTextAsync(path, Original);
        var store = new PlayerRuntimeSettingsStore(path);

        var global = await store.GetEffectiveTimeoutAsync(
            "source-a", sourceDeclaredTimeout: null, CancellationToken.None).WaitAsync(Budget);
        var declared = await store.GetEffectiveTimeoutAsync(
            "source-a", TimeSpan.FromSeconds(55), CancellationToken.None).WaitAsync(Budget);

        Assert.Equal(TimeSpan.FromSeconds(30), global.Timeout);
        Assert.IsAssignableFrom<JsonException>(global.FallbackReason);
        Assert.Equal(TimeSpan.FromSeconds(55), declared.Timeout);
        Assert.IsAssignableFrom<JsonException>(declared.FallbackReason);
        Assert.Equal(Original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task CallerCancellationRemainsCancellationAndLeavesTheFileUnchanged()
    {
        using var paths = new MockAppDataPaths();
        var path = Path.Combine(paths.RootDirectory, "runtime.json");
        const string Original = "{broken-json";
        await File.WriteAllTextAsync(path, Original);
        var store = new PlayerRuntimeSettingsStore(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.ReadAsync(cancellation.Token).WaitAsync(Budget));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.WriteAsync(ExistingSettings(), cancellation.Token).WaitAsync(Budget));

        Assert.Equal(Original, await File.ReadAllTextAsync(path));
    }

    private static PlayerRuntimeSettings ExistingSettings()
    {
        var settings = PlayerRuntimeSettings.CreateDefault();
        settings.GlobalTimeoutSeconds = 40;
        settings.SourceTimeoutSeconds["source-a"] = 70;
        return settings;
    }

    private static void AssertDefaultSettings(PlayerRuntimeSettings settings)
    {
        Assert.Equal(1, settings.SchemaVersion);
        Assert.Equal(30, settings.GlobalTimeoutSeconds);
        Assert.Empty(settings.SourceTimeoutSeconds);
    }
}
