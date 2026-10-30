using AniMeido.App.Services;
using Microsoft.UI.Xaml;

namespace AniMeido.Tests;

public sealed class LegacyDesktopSettingsImporterTests
{
    [Fact]
    public async Task Import_PersistsValuesAndMarkerTogetherAndDoesNotReadLegacyAgain()
    {
        using var paths = new MockAppDataPaths();
        var store = new DesktopSettingsStore(paths);
        var importer = Importer(store, "Dark", true, true);

        var imported = await importer.ImportAsync();
        Assert.Equal("Dark", imported.ThemeSetting);
        Assert.True(imported.PrivacyAccepted);
        Assert.True(imported.AnnouncementShown);
        Assert.True(imported.LegacyUiSettingsImported);
        var reopened = new LegacyDesktopSettingsImporter(new DesktopSettingsStore(paths),
            () => throw new InvalidOperationException("Legacy must not be read again"));
        Assert.Equal(imported, await reopened.ImportAsync());
    }

    [Fact]
    public async Task Import_ExplicitFalseAndFollowSystemOverrideLegacyValues()
    {
        using var paths = new MockAppDataPaths();
        var store = new DesktopSettingsStore(paths);
        await store.UpdateAsync(value => value with
        {
            ThemeSetting = "Default",
            PrivacyAccepted = false,
            AnnouncementShown = false,
            KeepInTrayOnClose = true,
        });

        var result = await Importer(store, "Dark", true, true).ImportAsync();

        Assert.Equal("Default", result.ThemeSetting);
        Assert.False(result.PrivacyAccepted);
        Assert.False(result.AnnouncementShown);
        Assert.True(result.KeepInTrayOnClose);
        Assert.True(result.LegacyUiSettingsImported);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    [InlineData(17)]
    public async Task Import_InvalidOrMissingThemeUsesFollowSystem(object? oldTheme)
    {
        using var paths = new MockAppDataPaths();
        var result = await Importer(new DesktopSettingsStore(paths), oldTheme, false, false).ImportAsync();

        Assert.Equal("Default", result.ThemeSetting);
        Assert.False(result.PrivacyAccepted);
        Assert.False(result.AnnouncementShown);
    }

    [Fact]
    public async Task Import_PreparedOldValuesDoNotOverwriteAChoiceSavedWhileReading()
    {
        using var paths = new MockAppDataPaths();
        var store = new DesktopSettingsStore(paths);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var legacy = new TaskCompletionSource<LegacyDesktopSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
        var importer = new LegacyDesktopSettingsImporter(store, () =>
        {
            reading.SetResult();
            return legacy.Task;
        });
        var importing = importer.ImportAsync();
        try
        {
            await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await store.UpdateAsync(value => value with { ThemeSetting = "Default", PrivacyAccepted = false });
        }
        finally
        {
            legacy.TrySetResult(new LegacyDesktopSettings("Dark", true, true));
            await Task.WhenAny(importing).WaitAsync(TimeSpan.FromSeconds(5));
            await ((Task)importing).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }

        var result = await importing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Default", result.ThemeSetting);
        Assert.False(result.PrivacyAccepted);
        Assert.True(result.AnnouncementShown);
        Assert.True(result.LegacyUiSettingsImported);
    }

    [Fact]
    public async Task Import_ReadFailureDoesNotPretendLegacyValuesWereAbsent()
    {
        using var paths = new MockAppDataPaths();
        var store = new DesktopSettingsStore(paths);
        var importer = new LegacyDesktopSettingsImporter(store, () =>
            Task.FromException<LegacyDesktopSettings>(new IOException("legacy read failed")));

        await Assert.ThrowsAsync<IOException>(() => importer.ImportAsync());
        var unchanged = await store.LoadAsync();
        Assert.Null(unchanged.PrivacyAccepted);
        Assert.False(unchanged.LegacyUiSettingsImported);
        Assert.True((await Importer(store, "Light", true, false).ImportAsync()).PrivacyAccepted);
    }

    [Fact]
    public async Task Import_WriteFailurePreservesFileAndRetriesTheWholeImport()
    {
        using var paths = new MockAppDataPaths();
        var store = new DesktopSettingsStore(paths);
        await store.UpdateAsync(value => value with { UpdateChannel = "preview" });
        var temporaryPath = Path.Combine(paths.RootDirectory, "desktop-settings.json.tmp");
        Directory.CreateDirectory(temporaryPath);
        var importer = Importer(store, "Dark", true, true);

        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => importer.ImportAsync());
        var unchanged = await store.LoadAsync();
        Assert.Equal("preview", unchanged.UpdateChannel);
        Assert.Null(unchanged.ThemeSetting);
        Assert.Null(unchanged.PrivacyAccepted);
        Assert.False(unchanged.LegacyUiSettingsImported);

        Directory.Delete(temporaryPath);
        var result = await importer.ImportAsync();
        Assert.Equal("preview", result.UpdateChannel);
        Assert.True(result.LegacyUiSettingsImported);
        Assert.True(result.PrivacyAccepted);
    }

    [Fact]
    public async Task Services_UpdateOnlyTheirOwnFields()
    {
        using var paths = new MockAppDataPaths();
        var store = new DesktopSettingsStore(paths);
        await Importer(store, null, false, false).ImportAsync();
        await using var theme = new ThemeService(store);
        var privacy = new PrivacyService(store);

        await Task.WhenAll(
            theme.SetThemeAsync(ElementTheme.Dark),
            privacy.AcceptAsync(),
            store.UpdateAsync(value => value with { KeepInTrayOnClose = true, AnnouncementShown = true }));

        var saved = await new DesktopSettingsStore(paths).LoadAsync();
        Assert.Equal("Dark", saved.ThemeSetting);
        Assert.True(await privacy.IsAcceptedAsync());
        Assert.True(saved.AnnouncementShown);
        Assert.True(saved.KeepInTrayOnClose);
        Assert.True(saved.LegacyUiSettingsImported);
    }

    [Fact]
    public async Task Theme_SaveFailureDoesNotApplyAnUnpersistedChoice()
    {
        using var paths = new MockAppDataPaths();
        var store = new DesktopSettingsStore(paths);
        await using var theme = new ThemeService(store);
        await theme.SetThemeAsync(ElementTheme.Light);
        Directory.CreateDirectory(Path.Combine(paths.RootDirectory, "desktop-settings.json.tmp"));

        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => theme.SetThemeAsync(ElementTheme.Dark));

        Assert.Equal(ElementTheme.Light, theme.GetCurrentTheme());
        Assert.Equal("Light", (await store.LoadAsync()).ThemeSetting);
    }

    [Fact]
    public async Task ConcurrentImports_KeepTheFirstCommittedMigration()
    {
        using var paths = new MockAppDataPaths();
        var store = new DesktopSettingsStore(paths);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRead = new TaskCompletionSource<LegacyDesktopSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new LegacyDesktopSettingsImporter(store, () =>
        {
            entered.SetResult();
            return oldRead.Task;
        });
        var importing = first.ImportAsync();
        DesktopSettings committed;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            committed = await Importer(store, "Light", false, false).ImportAsync();
        }
        finally
        {
            oldRead.TrySetResult(new LegacyDesktopSettings("Dark", true, true));
            await Task.WhenAny(importing).WaitAsync(TimeSpan.FromSeconds(5));
            await ((Task)importing).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }

        Assert.Equal(committed, await importing.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static LegacyDesktopSettingsImporter Importer(
        DesktopSettingsStore store, object? theme, bool privacy, bool announcement)
        => new(store, () => Task.FromResult(new LegacyDesktopSettings(theme, privacy, announcement)));
}
