using AniMeido.App.Services;

namespace AniMeido.Tests;

public sealed class DesktopSettingsStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Load_OldJson_PreservesTraySettingAndDefaultsSkippedVersion(bool keepInTray)
    {
        using var paths = new MockAppDataPaths();
        var path = Path.Combine(paths.RootDirectory, "desktop-settings.json");
        await File.WriteAllTextAsync(path, $"{{\"KeepInTrayOnClose\":{keepInTray.ToString().ToLowerInvariant()}}}");

        var settings = await new DesktopSettingsStore(paths).LoadAsync();

        Assert.Equal(keepInTray, settings.KeepInTrayOnClose);
        Assert.Null(settings.SkippedUpdateVersion);
    }

    [Fact]
    public async Task Load_MissingFile_ReturnsDefaults()
    {
        using var paths = new MockAppDataPaths();

        var settings = await new DesktopSettingsStore(paths).LoadAsync();

        Assert.False(settings.KeepInTrayOnClose);
        Assert.Null(settings.SkippedUpdateVersion);
    }

    [Fact]
    public async Task Save_SkippedVersion_PreservesOtherSettingsAcrossReload()
    {
        using var paths = new MockAppDataPaths();
        var store = new DesktopSettingsStore(paths);
        await store.SaveAsync(new DesktopSettings { KeepInTrayOnClose = true });
        var original = await store.LoadAsync();

        await store.SaveAsync(original with { SkippedUpdateVersion = "2.2.0" });
        var reloaded = await new DesktopSettingsStore(paths).LoadAsync();

        Assert.True(reloaded.KeepInTrayOnClose);
        Assert.Equal("2.2.0", reloaded.SkippedUpdateVersion);
    }

    [Fact]
    public async Task Update_TraySetting_PreservesPreviouslySkippedVersion()
    {
        using var paths = new MockAppDataPaths();
        var store = new DesktopSettingsStore(paths);
        await store.SaveAsync(new DesktopSettings { SkippedUpdateVersion = "2.2.0" });

        await store.UpdateAsync(settings => settings with { KeepInTrayOnClose = true });
        var reloaded = await store.LoadAsync();

        Assert.True(reloaded.KeepInTrayOnClose);
        Assert.Equal("2.2.0", reloaded.SkippedUpdateVersion);
    }

    [Fact]
    public async Task Update_ClearSkippedVersion_PreservesTraySetting()
    {
        using var paths = new MockAppDataPaths();
        var store = new DesktopSettingsStore(paths);
        await store.SaveAsync(new DesktopSettings { KeepInTrayOnClose = true, SkippedUpdateVersion = "2.2.0" });

        await store.UpdateAsync(settings => settings with { SkippedUpdateVersion = null });
        var reloaded = await new DesktopSettingsStore(paths).LoadAsync();

        Assert.True(reloaded.KeepInTrayOnClose);
        Assert.Null(reloaded.SkippedUpdateVersion);
    }

    [Fact]
    public async Task Update_ConcurrentPreferences_DoesNotLoseEitherChange()
    {
        using var paths = new MockAppDataPaths();
        var store = new DesktopSettingsStore(paths);

        await Task.WhenAll(
            store.UpdateAsync(settings => settings with { SkippedUpdateVersion = "2.2.0" }),
            store.UpdateAsync(settings => settings with { KeepInTrayOnClose = true }));
        var reloaded = await new DesktopSettingsStore(paths).LoadAsync();

        Assert.True(reloaded.KeepInTrayOnClose);
        Assert.Equal("2.2.0", reloaded.SkippedUpdateVersion);
    }
}
