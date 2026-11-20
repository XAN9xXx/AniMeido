using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Player.Playback;
using AniMeido.Plugin.Player.Services;
using AniMeido.Plugin.Player.Sources;
using System.Reflection;

namespace AniMeido.Tests;

[Trait("Category", "Mechanism")]
public sealed class PlayerWindowManagerLifetimeTests
{
    [Theory]
    [InlineData("OnPlayerWindowClosed")]
    [InlineData("OnSettingsWindowClosed")]
    public async Task LaunchReachesSourcesForAnotherAnimeAfterWindowClosed(string closedHandler)
    {
        // 经过真实播放入口，在创建原生窗口前停下；模拟的是窗口已完成收尾的通知。
        var stopBeforeCreatingWindow = new InvalidDataException("Synthetic source failure before window creation.");
        await using var manager = CreateManager(() => throw stopBeforeCreatingWindow);
        var first = new AnimePlaybackContext(42, "First anime");
        var next = new AnimePlaybackContext(43, "Next anime");

        Assert.Same(stopBeforeCreatingWindow,
            await Assert.ThrowsAsync<InvalidDataException>(() => manager.LaunchAsync(first)));

        NotifyWindowClosed(manager, closedHandler);

        Assert.Same(stopBeforeCreatingWindow,
            await Assert.ThrowsAsync<InvalidDataException>(() => manager.LaunchAsync(next)));
    }

    [Fact]
    public async Task HostDisposalStillRejectsPlaybackAndSettingsAfterWindowClosed()
    {
        await using var manager = CreateManager(() => throw new InvalidDataException("Sources must not be loaded."));
        NotifyWindowClosed(manager, "OnPlayerWindowClosed");

        await manager.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => manager.LaunchAsync(new AnimePlaybackContext(43, "Next anime")));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => manager.OpenSettingsAsync(PlayerWindowManager.PlayerSettingsId));
    }

    private static PlayerWindowManager CreateManager(Func<IEnumerable<IOnlineAnimeSource>> loadSources)
        => new(new OnlineSourceCatalog(loadSources), null!, null!, null!, null!, null!, null!,
            new PlayerExperienceSettingsStore(path: null), null!);

    private static void NotifyWindowClosed(PlayerWindowManager manager, string handler)
        => typeof(PlayerWindowManager).GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(manager, [null, null]);
}
