using AniMeido.Plugin.Player.Playback;
using AniMeido.Plugin.Player.Sources;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class PlayerPublicIdMigrationTests : DbTestBase
{
    [Fact]
    public async Task BothStores_MigrateChainsAndNegativeIdsExactlyOnce()
    {
        var (map, sources, settings) = await SeedAsync();
        var source = new SourceMappingStore(sources, map);
        await source.EnsurePublicIdsAsync(default);
        Assert.Equal("first", await source.GetAsync(2, "site", default));
        Assert.Equal("second", await source.GetAsync(3, "site", default));
        Assert.Equal("unmapped", await source.GetAsync(-4, "site", default));
        Assert.Equal("player-only", await source.GetAsync(-99, "site", default));
        var experience = new PlayerExperienceSettingsStore(settings, map);
        var value = await experience.ReadAsync(default);
        Assert.Equal(9, value.IdMigrationVersion);
        Assert.Equal(65, value.Volume);
        Assert.Equal("route-first", value.PreferredRouteByAnime["2"]);
        Assert.Equal("route-second", value.PreferredRouteByAnime["3"]);
        Assert.Equal("route-unmapped", value.PreferredRouteByAnime["-4"]);
        Assert.Equal("route-only", value.PreferredRouteByAnime["-99"]);
        var sourceBefore = await File.ReadAllTextAsync(sources);
        var settingsBefore = await File.ReadAllTextAsync(settings);
        await new SourceMappingStore(sources, map).EnsurePublicIdsAsync(default);
        await new PlayerExperienceSettingsStore(settings, map).ReadAsync(default);
        Assert.Equal(sourceBefore, await File.ReadAllTextAsync(sources));
        Assert.Equal(settingsBefore, await File.ReadAllTextAsync(settings));
        Assert.True(File.Exists(map));
    }

    [Fact]
    public async Task MissingFile_DoesNotMarkMigrationAndCanMigrateLater()
    {
        var (map, sources, _) = await SeedAsync();
        var json = await File.ReadAllTextAsync(map); File.Delete(map);
        var store = new SourceMappingStore(sources, map);
        Assert.Equal("first", await store.GetAsync(1, "site", default));
        await File.WriteAllTextAsync(map, json);
        await store.EnsurePublicIdsAsync(default);
        Assert.Equal("first", await store.GetAsync(2, "site", default));
    }

    [Fact]
    public async Task CorruptMigrationFile_DoesNotOverwriteEitherStore()
    {
        var (map, sources, settings) = await SeedAsync();
        var beforeSources = await File.ReadAllTextAsync(sources); var beforeSettings = await File.ReadAllTextAsync(settings);
        await File.WriteAllTextAsync(map, "broken-json");
        await Assert.ThrowsAsync<JsonException>(() => new SourceMappingStore(sources, map).EnsurePublicIdsAsync(default));
        await Assert.ThrowsAsync<JsonException>(() => new PlayerExperienceSettingsStore(settings, map).ReadAsync(default));
        Assert.Equal(beforeSources, await File.ReadAllTextAsync(sources));
        Assert.Equal(beforeSettings, await File.ReadAllTextAsync(settings));
    }

    [Fact]
    public async Task IndependentMarkers_ResumeAfterOnlySourceMappingCompleted()
    {
        var (map, sources, settings) = await SeedAsync();
        await new SourceMappingStore(sources, map).EnsurePublicIdsAsync(default);
        var migratedSources = await File.ReadAllTextAsync(sources);
        // 模拟在第一个 store 提交后退出，第二个 store 仍是旧格式。
        var result = await new PlayerExperienceSettingsStore(settings, map).ReadAsync(default);
        await new SourceMappingStore(sources, map).EnsurePublicIdsAsync(default);
        Assert.Equal(9, result.IdMigrationVersion);
        Assert.Equal(migratedSources, await File.ReadAllTextAsync(sources));
    }

    private async Task<(string Map, string Sources, string Settings)> SeedAsync()
    {
        var map = Path.Combine(Paths.RootDirectory, "id-migration-v9.json");
        var sources = Path.Combine(Paths.RootDirectory, "mappings.json");
        var settings = Path.Combine(Paths.RootDirectory, "experience.json");
        await File.WriteAllTextAsync(map, """{"schemaVersion":9,"workIds":{"1":2,"2":3,"4":-4}}""");
        await File.WriteAllTextAsync(sources, """{"1:site":"first","2:site":"second","4:site":"unmapped","99:site":"player-only"}""");
        await File.WriteAllTextAsync(settings, """
            {"SchemaVersion":1,"Volume":65,"PreferredRouteByAnime":{
                "1":"route-first","2":"route-second","4":"route-unmapped","99":"route-only"}}
            """);
        return (map, sources, settings);
    }
}
