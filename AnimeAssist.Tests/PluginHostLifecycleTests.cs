using AniMeido.App.Services;
using AniMeido.PluginProtocol;

namespace AniMeido.Tests;

public sealed class PluginHostLifecycleTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(-1, false)]
    public void ExitClassifier_DistinguishesNormalWindowClose(
        int exitCode,
        bool expected)
        => Assert.Equal(
            expected,
            PluginHostExitClassifier.IsNormal(exitCode));

    [Fact]
    public void ManifestSnapshot_ContainsOnlyRequestedPlugin()
    {
        var manifest = new PluginManifest
        {
            PluginId = "AniMeido.Plugin.Player",
            DisplayName = "在线播放器",
            Contributions = new PluginContributions
            {
                Commands =
                [
                    new PluginCommandContribution
                    {
                        Id = "open",
                        Title = "打开播放器",
                        Icon = "\uE768",
                    },
                ],
                Navigation =
                [
                    new PluginNavigationContribution
                    {
                        Command = "open",
                    },
                ],
                Settings =
                [
                    new PluginSettingsContribution
                    {
                        Id = "player.settings",
                        Title = "播放源",
                        Icon = "\uE713",
                    },
                ],
                Capabilities =
                [
                    PluginHostProtocol.AnimePlaybackCapability,
                ],
            },
        };

        var snapshot = PluginHostSession.CreateManifestSnapshot(manifest);

        var command = Assert.Single(snapshot.NavigationCommands);
        Assert.Equal(manifest.PluginId, command.PluginId);
        var settings = Assert.Single(snapshot.Settings);
        Assert.Equal(manifest.PluginId, settings.PluginId);
        Assert.Contains(
            PluginHostProtocol.AnimePlaybackCapability,
            snapshot.Capabilities);
        Assert.Empty(snapshot.Failures);
    }
}
