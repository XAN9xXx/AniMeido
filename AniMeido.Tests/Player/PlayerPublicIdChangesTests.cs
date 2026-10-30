using AniMeido.Plugin.Player.Playback;

namespace AniMeido.Tests;

public sealed class PlayerPublicIdChangesTests
{
    private const string ReusedTarget = """[{"seq":1,"from":2,"to":3,"at":"2026-10-28"},{"seq":2,"from":1,"to":2,"at":"2026-10-28"}]""";
    private const string ExtendedChain = """[{"seq":1,"from":1,"to":2,"at":"2026-10-28"},{"seq":2,"from":2,"to":3,"at":"2026-10-28"},{"seq":3,"from":4,"to":2,"at":"2026-10-28"}]""";
    private const string ReturnedIdentity = """[{"seq":1,"from":1,"to":2,"at":"2026-10-28"},{"seq":2,"from":2,"to":1,"at":"2026-10-28"}]""";

    [Theory]
    [InlineData(ReusedTarget, 1, 2)]
    [InlineData(ReusedTarget, 2, 2)]
    [InlineData(ReusedTarget, 3, 3)]
    [InlineData(ExtendedChain, 1, 3)]
    [InlineData(ExtendedChain, 2, 2)]
    [InlineData(ExtendedChain, 4, 2)]
    [InlineData(ReturnedIdentity, 1, 1)]
    [InlineData(ReturnedIdentity, 2, 1)]
    [InlineData(ExtendedChain, 99, 99)]
    [InlineData("[]", 42, 42)]
    [InlineData("[]", -42, -42)]
    public async Task ResolveCurrentId_UsesTheLatestMentionBeforeFollowingLaterChanges(
        string log, int animeId, int expected)
    {
        using var paths = new MockAppDataPaths();
        var path = Path.Combine(paths.RootDirectory, "id-remap-log.json");
        await File.WriteAllTextAsync(path, log);

        var changes = await PlayerPublicIdChanges.ReadAsync(null, path, default);

        Assert.Equal(expected, changes.ResolveCurrentId(animeId));
    }
}
