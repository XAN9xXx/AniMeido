using AniMeido.Plugin.Player.Models;
using AniMeido.Plugin.Player.Playback;
using AniMeido.Plugin.Player.Sources;

namespace AniMeido.Tests;

public sealed class PlayerExperienceTests
{
    [Fact]
    public void EpisodeGroups_CollapseEquivalentEpisodesAndOrderNumerically()
    {
        SourceEpisodeEntry[] entries =
        [
            CreateEntry("source-a", "第02集", "线路 A"),
            CreateEntry("source-b", "第1话", "线路 B"),
            CreateEntry("source-c", "第01集", "线路 C"),
        ];

        var groups = PlayerEpisodeGroup.Create(entries);

        Assert.Equal(2, groups.Count);
        Assert.Equal("第 1 集", groups[0].DisplayTitle);
        Assert.Equal(2, groups[0].Routes.Count);
        Assert.Equal("第 2 集", groups[1].DisplayTitle);
    }

    [Fact]
    public void EpisodeGroups_PrioritizeHealthyRoutes()
    {
        var unhealthy = CreateEntry("source-a", "第01集", "线路 A");
        var healthy = CreateEntry("source-b", "第01集", "线路 B");
        var health = new Dictionary<string, RouteHealthRecord>
        {
            [PlayerEpisodeGroup.GetRouteKey(unhealthy)] = new()
            {
                ConsecutiveFailures = 2,
            },
            [PlayerEpisodeGroup.GetRouteKey(healthy)] = new()
            {
                SuccessCount = 2,
                LastLatencyMilliseconds = 500,
            },
        };

        var group = Assert.Single(
            PlayerEpisodeGroup.Create([unhealthy, healthy], health));

        Assert.Same(healthy, group.Routes[0]);
    }

    [Fact]
    public async Task ExperienceStore_PersistsPreferencesAndRouteHealth()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"animeido-experience-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "experience.json");
        try
        {
            var store = new PlayerExperienceSettingsStore(path);
            await store.UpdatePreferencesAsync(
                volume: 65,
                muted: true,
                speed: 1.25,
                autoFallbackEnabled: true,
                windowWidth: 1280,
                windowHeight: 720,
                cancellationToken: CancellationToken.None);
            await store.RecordRouteResultAsync(
                42,
                "source\u001froute",
                succeeded: true,
                TimeSpan.FromMilliseconds(450),
                CancellationToken.None);

            var settings = await store.ReadAsync(CancellationToken.None);

            Assert.Equal(65, settings.Volume);
            Assert.True(settings.IsMuted);
            Assert.Equal(1.25, settings.Speed);
            Assert.True(settings.AutoFallbackEnabled);
            Assert.Equal(
                "source\u001froute",
                settings.PreferredRouteByAnime["42"]);
            Assert.Equal(
                1,
                settings.RouteHealth["source\u001froute"].SuccessCount);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static SourceEpisodeEntry CreateEntry(
        string sourceId,
        string title,
        string route)
        => new(
            sourceId,
            new SourceEpisode(
                sourceId,
                $"{sourceId}:{title}",
                title,
                route));
}
