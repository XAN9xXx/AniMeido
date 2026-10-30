using AniMeido.Plugin.Player;
using AniMeido.Plugin.Player.Playback;
using AniMeido.Plugin.Player.Sources;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class PlayerIdRemapTests : DbTestBase
{
    [Fact]
    public async Task BothStores_ApplySequenceOnce_TargetWinsAndNewEntriesCanArriveLater()
    {
        var (log, mappings, settings) = await SeedAsync();
        var source = new SourceMappingStore(mappings, remapPath: log);
        var experience = new PlayerExperienceSettingsStore(settings, remapPath: log);
        await source.EnsurePublicIdsAsync(default);
        var value = (await experience.ReadAndApplyPublicIdsAsync(default)).Settings;
        Assert.Equal("target", await source.GetAsync(2, "a", default));
        Assert.Equal("other-source", await source.GetAsync(2, "b", default));
        Assert.Null(await source.GetAsync(-1, "a", default));
        Assert.Equal("target-route", value.PreferredRouteByAnime["2"]);
        Assert.Equal("negative-route", value.PreferredRouteByAnime["-4"]);
        Assert.Equal(2, value.LastRemapSeq);
        var sourceJson = await File.ReadAllTextAsync(mappings); var settingsJson = await File.ReadAllTextAsync(settings);
        await new SourceMappingStore(mappings, remapPath: log).EnsurePublicIdsAsync(default);
        await new PlayerExperienceSettingsStore(settings, remapPath: log).ReadAndApplyPublicIdsAsync(default);
        Assert.Equal(sourceJson, await File.ReadAllTextAsync(mappings)); Assert.Equal(settingsJson, await File.ReadAllTextAsync(settings));
        await File.WriteAllTextAsync(log, """[{"seq":1,"from":-1,"to":2,"at":"2026-10-20"},{"seq":2,"from":3,"to":-4,"at":"2026-10-20"},{"seq":3,"from":2,"to":5,"at":"2026-10-20"}]""");
        await source.EnsurePublicIdsAsync(default); value = (await experience.ReadAndApplyPublicIdsAsync(default)).Settings;
        Assert.Equal("target", await source.GetAsync(5, "a", default)); Assert.Equal("target-route", value.PreferredRouteByAnime["5"]);
        Assert.Equal(3, value.LastRemapSeq); Assert.True(File.Exists(log));
    }

    [Fact]
    public async Task PluginInitializationOnlyRegistersStores_OpeningBoundaryResumesIndependentMarkers()
    {
        var (log, mappings, settings) = await SeedAsync();
        var source = new SourceMappingStore(mappings, remapPath: log);
        var experience = new PlayerExperienceSettingsStore(settings, remapPath: log);
        await source.EnsurePublicIdsAsync(default);
        var beforeMappings = await File.ReadAllTextAsync(mappings);
        var beforeSettings = await File.ReadAllTextAsync(settings);
        var services = new ServiceCollection();

        await new PlayerPlugin(source, experience).InitializeAsync(services);

        Assert.Equal(beforeMappings, await File.ReadAllTextAsync(mappings));
        Assert.Equal(beforeSettings, await File.ReadAllTextAsync(settings));
        using var provider = services.BuildServiceProvider();
        Assert.Same(source, provider.GetRequiredService<SourceMappingStore>());
        Assert.Same(experience, provider.GetRequiredService<PlayerExperienceSettingsStore>());

        var result = await experience.ReadAndApplyPublicIdsAsync(default);
        Assert.Equal(2, result.Settings.LastRemapSeq);
        Assert.Equal(beforeMappings, await File.ReadAllTextAsync(mappings));
    }

    [Theory]
    [InlineData("broken")]
    [InlineData("[{\"seq\":2,\"from\":1,\"to\":2,\"at\":\"2026-10-20\"}]")]
    public async Task InvalidLog_DoesNotOverwriteAnyStore(string invalid)
    {
        var (log, mappings, settings) = await SeedAsync();
        var a = await File.ReadAllTextAsync(mappings); var b = await File.ReadAllTextAsync(settings);
        await File.WriteAllTextAsync(log, invalid);
        Assert.NotNull(await Record.ExceptionAsync(() => new SourceMappingStore(mappings, remapPath: log).EnsurePublicIdsAsync(default)));
        Assert.NotNull(await Record.ExceptionAsync(() => new PlayerExperienceSettingsStore(settings, remapPath: log).ReadAndApplyPublicIdsAsync(default)));
        Assert.Equal(a, await File.ReadAllTextAsync(mappings)); Assert.Equal(b, await File.ReadAllTextAsync(settings));
    }

    [Fact]
    public async Task C2aMigrationRunsBeforeC2bRemapsInBothStores()
    {
        var (log, mappings, settings) = await SeedAsync();
        var map = Path.Combine(Paths.RootDirectory, "id-migration-v9.json");
        await File.WriteAllTextAsync(map, """{"schemaVersion":9,"workIds":{"7":-7}}""");
        await File.WriteAllTextAsync(log, """[{"seq":1,"from":-7,"to":2,"at":"2026-10-20"},{"seq":2,"from":3,"to":-4,"at":"2026-10-20"}]""");
        await File.WriteAllTextAsync(mappings, """{"7:a":"old-route"}""");
        await File.WriteAllTextAsync(settings, """{"SchemaVersion":1,"PreferredRouteByAnime":{"7":"old-route"}}""");
        var source = new SourceMappingStore(mappings, map, log); await source.EnsurePublicIdsAsync(default);
        Assert.Equal("old-route", await source.GetAsync(2, "a", default));
        var value = (await new PlayerExperienceSettingsStore(settings, map, log).ReadAndApplyPublicIdsAsync(default)).Settings;
        Assert.Equal("old-route", value.PreferredRouteByAnime["2"]); Assert.Equal(9, value.IdMigrationVersion); Assert.Equal(2, value.LastRemapSeq);
    }

    [Fact]
    public async Task MissingLog_DoesNotAdvanceSeq_AndSavingKeepsSeqWithoutC2aMarker()
    {
        var (log, mappings, settings) = await SeedAsync(); var text = await File.ReadAllTextAsync(log); File.Delete(log);
        // 兼容尚未带 C2a 包装的源映射文件；后续普通保存也不能丢掉 seq。
        await File.WriteAllTextAsync(mappings, """{"-1:a":"source"}""");
        var source = new SourceMappingStore(mappings, remapPath: log); await source.EnsurePublicIdsAsync(default);
        Assert.Equal("source", await source.GetAsync(-1, "a", default));
        await File.WriteAllTextAsync(log, text); await source.EnsurePublicIdsAsync(default);
        await source.SetAsync(9, "a", "new", default);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(mappings)); Assert.Equal(2, json.RootElement.GetProperty("LastRemapSeq").GetInt64());
        var settingsValue = (await new PlayerExperienceSettingsStore(settings, remapPath: log).ReadAndApplyPublicIdsAsync(default)).Settings; Assert.Equal(2, settingsValue.LastRemapSeq);
    }

    private async Task<(string Log, string Mappings, string Settings)> SeedAsync()
    {
        var log = Path.Combine(Paths.RootDirectory, "id-remap-log.json");
        var mappings = Path.Combine(Paths.RootDirectory, "mappings.json");
        var settings = Path.Combine(Paths.RootDirectory, "experience.json");
        await File.WriteAllTextAsync(log, """[{"seq":1,"from":-1,"to":2,"at":"2026-10-20"},{"seq":2,"from":3,"to":-4,"at":"2026-10-20"}]""");
        await File.WriteAllTextAsync(mappings, """{"IdMigrationVersion":9,"Mappings":{"-1:a":"source","-1:b":"other-source","2:a":"target","3:a":"negative"}}""");
        await File.WriteAllTextAsync(settings, """{"SchemaVersion":1,"IdMigrationVersion":9,"PreferredRouteByAnime":{"-1":"source-route","2":"target-route","3":"negative-route"}}""");
        return (log, mappings, settings);
    }
}
