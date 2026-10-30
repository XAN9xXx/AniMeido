using AniMeido.Plugin.Player.Models;
using AniMeido.Plugin.Player.Playback;
using AniMeido.Plugin.Player.Sources;
using System.Reflection;

namespace AniMeido.Tests;

public sealed class PlayerExperienceTests
{
    [Theory]
    [InlineData("read")]
    [InlineData("apply")]
    [InlineData("preferences")]
    [InlineData("route")]
    [Trait("Category", "Mechanism")]
    public async Task CompleteSettingsOperationDoesNotRequireTheCallingUiContext(string action)
    {
        using var paths = new MockAppDataPaths();
        var store = new PlayerExperienceSettingsStore(Path.Combine(paths.RootDirectory, "experience.json"));
        var gate = (SemaphoreSlim)typeof(PlayerExperienceSettingsStore)
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
                "read" => store.ReadAsync(default),
                "apply" => store.ReadAndApplyPublicIdsAsync(default),
                "preferences" => store.UpdatePreferencesAsync(65, true, 1.25, true, 1280, 720, default),
                "route" => store.RecordRouteResultAsync(42, "source\u001froute", true, TimeSpan.FromMilliseconds(450), default),
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
            await operation.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, ui.PostCount);
        }
        finally
        {
            ui.Resume();
            await Task.WhenAny(operation).WaitAsync(TimeSpan.FromSeconds(5));
            await operation.ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }

        var settings = (await store.ReadAsync(default)).Settings;
        if (action == "preferences")
            Assert.Equal(65, settings.Volume);
        if (action == "route")
            Assert.Equal("source\u001froute", settings.PreferredRouteByAnime["42"]);
    }

    [Fact]
    public async Task PrecancelledSettingsWriteDoesNotCreateAFile()
    {
        using var paths = new MockAppDataPaths();
        var path = Path.Combine(paths.RootDirectory, "experience.json");
        var store = new PlayerExperienceSettingsStore(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.UpdatePreferencesAsync(
            65, true, 1.25, true, 1280, 720, cancellation.Token));
        Assert.False(File.Exists(path));
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task QueuedPreferenceWritesKeepTheLatestChoiceAndBothRouteResults()
    {
        using var paths = new MockAppDataPaths();
        var store = new PlayerExperienceSettingsStore(Path.Combine(paths.RootDirectory, "experience.json"));
        var gate = (SemaphoreSlim)typeof(PlayerExperienceSettingsStore)
            .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        await gate.WaitAsync();
        Task[] operations;
        try
        {
            operations =
            [
                store.UpdatePreferencesAsync(40, false, 1, false, 1200, 780, default),
                store.RecordRouteResultAsync(42, "source\u001froute", true, TimeSpan.FromMilliseconds(450), default),
                store.RecordRouteResultAsync(42, "source\u001froute", false, TimeSpan.FromMilliseconds(900), default),
                store.UpdatePreferencesAsync(65, true, 1.25, true, 1280, 720, default),
            ];
        }
        finally
        {
            gate.Release();
        }

        await Task.WhenAll(operations).WaitAsync(TimeSpan.FromSeconds(5));
        var settings = (await store.ReadAsync(default)).Settings;
        Assert.Equal(65, settings.Volume);
        Assert.True(settings.IsMuted);
        Assert.Equal(1.25, settings.Speed);
        Assert.True(settings.AutoFallbackEnabled);
        var health = settings.RouteHealth["source\u001froute"];
        Assert.Equal(1, health.SuccessCount);
        Assert.Equal(1, health.FailureCount);
        Assert.Equal(1, health.ConsecutiveFailures);
        Assert.Equal("source\u001froute", settings.PreferredRouteByAnime["42"]);
    }

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
            var committed = await store.RecordRouteResultAsync(
                42,
                "source\u001froute",
                succeeded: true,
                TimeSpan.FromMilliseconds(450),
                CancellationToken.None);

            var settings = (await store.ReadAsync(CancellationToken.None)).Settings;

            Assert.Equal(settings.Volume, committed.Volume);
            Assert.Equal(settings.IsMuted, committed.IsMuted);
            Assert.Equal(settings.Speed, committed.Speed);
            Assert.Equal(settings.AutoFallbackEnabled, committed.AutoFallbackEnabled);
            Assert.Equal(settings.PreferredRouteByAnime, committed.PreferredRouteByAnime);
            Assert.Equal(settings.RouteHealth, committed.RouteHealth);
            committed.PreferredRouteByAnime.Clear();
            Assert.Single((await store.ReadAsync(CancellationToken.None)).Settings.PreferredRouteByAnime);

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
