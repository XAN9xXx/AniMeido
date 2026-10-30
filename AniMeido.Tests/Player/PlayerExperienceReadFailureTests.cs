using AniMeido.Plugin.Player.Playback;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class PlayerExperienceReadFailureTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    public enum InvalidSettings
    {
        Json,
        Schema,
        Directory,
    }

    [Theory]
    [InlineData(InvalidSettings.Json, false)]
    [InlineData(InvalidSettings.Json, true)]
    [InlineData(InvalidSettings.Schema, false)]
    [InlineData(InvalidSettings.Schema, true)]
    [InlineData(InvalidSettings.Directory, false)]
    [InlineData(InvalidSettings.Directory, true)]
    public async Task ReadFallbackDoesNotAuthorizeSavingOverInvalidSettings(
        InvalidSettings invalid, bool routeResult)
    {
        using var paths = new MockAppDataPaths();
        var path = Path.Combine(paths.RootDirectory, "experience.json");
        string? original = null;
        if (invalid == InvalidSettings.Directory)
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            var settings = ExistingSettings();
            settings.SchemaVersion = 2;
            original = invalid == InvalidSettings.Json ? "{broken-json" : JsonSerializer.Serialize(settings);
            await File.WriteAllTextAsync(path, original);
        }
        var store = new PlayerExperienceSettingsStore(path);

        AssertDefaultSettings((await store.ReadAsync(default).WaitAsync(Budget)).Settings);
        var error = await Record.ExceptionAsync(() => SaveAsync(store, routeResult).WaitAsync(Budget));

        if (invalid == InvalidSettings.Directory)
        {
            Assert.True(Directory.Exists(path));
            Assert.Empty(Directory.EnumerateFileSystemEntries(path));
        }
        else
        {
            Assert.Equal(original, await File.ReadAllTextAsync(path));
        }
        Assert.NotNull(error);
        Assert.False(error is OperationCanceledException);

        if (invalid == InvalidSettings.Directory)
            Directory.Delete(path);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(ExistingSettings()));
        await SaveAsync(store, routeResult).WaitAsync(Budget);
        await AssertSavedAsync(store, routeResult, hadExistingSettings: true);
    }

    [Theory]
    [InlineData(InvalidSettings.Json, false)]
    [InlineData(InvalidSettings.Json, true)]
    [InlineData(InvalidSettings.Schema, false)]
    [InlineData(InvalidSettings.Schema, true)]
    public async Task ReadFallbackStillAllowsPlaybackWhenPublicIdChangesArePending(
        InvalidSettings invalid, bool migration)
    {
        using var paths = new MockAppDataPaths();
        var path = Path.Combine(paths.RootDirectory, "experience.json");
        var changesPath = Path.Combine(paths.RootDirectory, migration ? "migration.json" : "remaps.json");
        var settings = ExistingSettings();
        settings.SchemaVersion = 2;
        settings.IdMigrationVersion = 0;
        settings.LastRemapSeq = 0;
        var original = invalid == InvalidSettings.Json ? "{broken-json" : JsonSerializer.Serialize(settings);
        await File.WriteAllTextAsync(path, original);
        await File.WriteAllTextAsync(changesPath, migration
            ? """{"schemaVersion":9,"workIds":{"11":101}}"""
            : """[{"seq":1,"from":11,"to":101,"at":"2026-10-27T12:00:00Z"}]""");
        var store = new PlayerExperienceSettingsStore(path,
            migrationPath: migration ? changesPath : null,
            remapPath: migration ? null : changesPath);

        AssertDefaultSettings((await store.ReadAsync(default).WaitAsync(Budget)).Settings);
        AssertDefaultSettings((await store.ReadAndApplyPublicIdsAsync(default).WaitAsync(Budget)).Settings);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        foreach (var routeResult in new[] { false, true })
        {
            var error = await Record.ExceptionAsync(() => SaveAsync(store, routeResult).WaitAsync(Budget));
            Assert.NotNull(error);
            Assert.False(error is OperationCanceledException);
            Assert.Equal(original, await File.ReadAllTextAsync(path));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadIoFallbackKeepsTheOriginalAndAllowsSavingAfterTheHandleIsReleased(bool routeResult)
    {
        using var paths = new MockAppDataPaths();
        var path = Path.Combine(paths.RootDirectory, "experience.json");
        var original = JsonSerializer.Serialize(ExistingSettings());
        await File.WriteAllTextAsync(path, original);
        var store = new PlayerExperienceSettingsStore(path);
        Exception? error;

        // 用临时文件的共享句柄制造真实读取失败，释放后核对原文并重试。
        using (var unreadable = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
            FileShare.Write | FileShare.Delete))
        {
            AssertDefaultSettings((await store.ReadAsync(default).WaitAsync(Budget)).Settings);
            error = await Record.ExceptionAsync(() => SaveAsync(store, routeResult).WaitAsync(Budget));
        }

        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.True(error is IOException or UnauthorizedAccessException);
        await SaveAsync(store, routeResult).WaitAsync(Budget);
        await AssertSavedAsync(store, routeResult, hadExistingSettings: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingSettingsStillAllowTheFirstSave(bool routeResult)
    {
        using var paths = new MockAppDataPaths();
        var path = Path.Combine(paths.RootDirectory, "experience.json");
        var store = new PlayerExperienceSettingsStore(path);

        AssertDefaultSettings((await store.ReadAsync(default)).Settings);
        Assert.False(File.Exists(path));
        await SaveAsync(store, routeResult).WaitAsync(Budget);

        Assert.True(File.Exists(path));
        await AssertSavedAsync(store, routeResult, hadExistingSettings: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalSavePreservesTheOtherSettingsAndRouteRecords(bool routeResult)
    {
        using var paths = new MockAppDataPaths();
        var path = Path.Combine(paths.RootDirectory, "experience.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(ExistingSettings()));
        var store = new PlayerExperienceSettingsStore(path);

        await SaveAsync(store, routeResult).WaitAsync(Budget);

        await AssertSavedAsync(store, routeResult, hadExistingSettings: true);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("preferences")]
    [InlineData("route")]
    public async Task CancelledOperationRemainsCancellationAndKeepsTheOriginal(string action)
    {
        using var paths = new MockAppDataPaths();
        var path = Path.Combine(paths.RootDirectory, "experience.json");
        const string original = "{broken-json";
        await File.WriteAllTextAsync(path, original);
        var store = new PlayerExperienceSettingsStore(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => (action switch
        {
            "read" => store.ReadAsync(cancellation.Token),
            "preferences" => SaveAsync(store, routeResult: false, cancellation.Token),
            "route" => SaveAsync(store, routeResult: true, cancellation.Token),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        }).WaitAsync(Budget));

        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    private static Task SaveAsync(
        PlayerExperienceSettingsStore store, bool routeResult, CancellationToken cancellationToken = default)
        => routeResult
            ? store.RecordRouteResultAsync(42, "new-route", true, TimeSpan.FromMilliseconds(450), cancellationToken)
            : store.UpdatePreferencesAsync(65, true, 1.25, true, 1280, 720, cancellationToken);

    private static PlayerExperienceSettings ExistingSettings() => new()
    {
        IdMigrationVersion = 9,
        LastRemapSeq = 7,
        Volume = 33,
        Speed = 1.5,
        WindowWidth = 1400,
        WindowHeight = 900,
        PreferredRouteByAnime = new() { ["11"] = "kept-route" },
        RouteHealth = new()
        {
            ["kept-route"] = new RouteHealthRecord
            {
                SuccessCount = 4,
                FailureCount = 2,
                ConsecutiveFailures = 1,
                LastLatencyMilliseconds = 123,
                LastSuccessUtc = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero),
            },
        },
    };

    private static void AssertDefaultSettings(PlayerExperienceSettings settings)
    {
        Assert.Equal(100, settings.Volume);
        Assert.Equal(1, settings.Speed);
        Assert.False(settings.IsMuted);
        Assert.False(settings.AutoFallbackEnabled);
        Assert.Equal(1200, settings.WindowWidth);
        Assert.Equal(780, settings.WindowHeight);
        Assert.Empty(settings.RouteHealth);
        Assert.Empty(settings.PreferredRouteByAnime);
    }

    private static async Task AssertSavedAsync(
        PlayerExperienceSettingsStore store, bool routeResult, bool hadExistingSettings)
    {
        var before = hadExistingSettings ? ExistingSettings() : new PlayerExperienceSettings();
        var saved = (await store.ReadAsync(default).WaitAsync(Budget)).Settings;
        Assert.Equal(1, saved.SchemaVersion);
        Assert.Equal(before.IdMigrationVersion, saved.IdMigrationVersion);
        Assert.Equal(before.LastRemapSeq, saved.LastRemapSeq);
        Assert.Equal(routeResult ? before.Volume : 65, saved.Volume);
        Assert.Equal(routeResult ? before.Speed : 1.25, saved.Speed);
        Assert.Equal(!routeResult, saved.IsMuted);
        Assert.Equal(!routeResult, saved.AutoFallbackEnabled);
        Assert.Equal(routeResult ? before.WindowWidth : 1280, saved.WindowWidth);
        Assert.Equal(routeResult ? before.WindowHeight : 720, saved.WindowHeight);
        Assert.Equal(before.RouteHealth.Count + (routeResult ? 1 : 0), saved.RouteHealth.Count);
        Assert.Equal(before.PreferredRouteByAnime.Count + (routeResult ? 1 : 0), saved.PreferredRouteByAnime.Count);
        foreach (var (key, health) in before.RouteHealth)
            Assert.Equal(health, saved.RouteHealth[key]);
        foreach (var (key, route) in before.PreferredRouteByAnime)
            Assert.Equal(route, saved.PreferredRouteByAnime[key]);
        if (routeResult)
        {
            Assert.Equal("new-route", saved.PreferredRouteByAnime["42"]);
            var health = saved.RouteHealth["new-route"];
            Assert.Equal(1, health.SuccessCount);
            Assert.Equal(0, health.FailureCount);
            Assert.Equal(0, health.ConsecutiveFailures);
            Assert.Equal(450, health.LastLatencyMilliseconds);
            Assert.NotNull(health.LastSuccessUtc);
            Assert.Null(health.LastFailureUtc);
        }
    }
}
