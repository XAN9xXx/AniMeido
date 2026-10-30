using AniMeido.Plugin.Player.Playback;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class PlayerExperienceReadResultTests
{
    [Theory]
    [InlineData("{broken-json")]
    [InlineData("{\"SchemaVersion\":2,\"Volume\":35}")]
    [InlineData("null")]
    public async Task InvalidSettingsReturnDefaultsAndTheCurrentReadFailure(string original)
    {
        using var paths = new MockAppDataPaths();
        var path = Path.Combine(paths.RootDirectory, "experience.json");
        await File.WriteAllTextAsync(path, original);
        var store = new PlayerExperienceSettingsStore(path);

        var result = await store.ReadAsync(default);

        Assert.Equal(100, result.Settings.Volume);
        Assert.Empty(result.Settings.PreferredRouteByAnime);
        if (original == "{broken-json")
            Assert.IsAssignableFrom<JsonException>(result.FallbackReason);
        else
            Assert.IsType<InvalidDataException>(result.FallbackReason);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task UnreadableSettingsReturnDefaultsAndTheIoFailure()
    {
        using var paths = new MockAppDataPaths();
        var path = Path.Combine(paths.RootDirectory, "experience.json");
        const string original = "{\"SchemaVersion\":1,\"Volume\":35}";
        await File.WriteAllTextAsync(path, original);
        var store = new PlayerExperienceSettingsStore(path);
        PlayerExperienceReadResult result;
        using (var unreadable = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
            FileShare.Write | FileShare.Delete))
        {
            result = await store.ReadAsync(default);
        }

        Assert.Equal(100, result.Settings.Volume);
        Assert.True(result.FallbackReason is IOException or UnauthorizedAccessException);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalAndMissingSettingsHaveNoFallbackReason(bool exists)
    {
        using var paths = new MockAppDataPaths();
        var path = Path.Combine(paths.RootDirectory, "experience.json");
        if (exists)
            await File.WriteAllTextAsync(path, "{\"SchemaVersion\":1,\"Volume\":35}");
        var store = new PlayerExperienceSettingsStore(path);

        var result = await store.ReadAsync(default);

        Assert.Equal(exists ? 35 : 100, result.Settings.Volume);
        Assert.Null(result.FallbackReason);
        Assert.Equal(exists, File.Exists(path));
    }

    [Fact]
    public async Task RepairingTheFileRemovesTheReasonFromTheNextReadOfTheSameStore()
    {
        using var paths = new MockAppDataPaths();
        var path = Path.Combine(paths.RootDirectory, "experience.json");
        await File.WriteAllTextAsync(path, "{broken-json");
        var store = new PlayerExperienceSettingsStore(path);
        var failed = await store.ReadAsync(default);
        Assert.IsAssignableFrom<JsonException>(failed.FallbackReason);

        await File.WriteAllTextAsync(path, "{\"SchemaVersion\":1,\"Volume\":35}");
        var repaired = await store.ReadAsync(default);

        Assert.Equal(35, repaired.Settings.Volume);
        Assert.Null(repaired.FallbackReason);
    }
}
