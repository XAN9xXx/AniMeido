using AniMeido.App.Services;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed partial class PublicIdMigrationTests
{
    [Fact]
    public async Task LegacyImport_UntraversableRuleKeepsRawJsonWithoutBlockingValidTracking()
    {
        await RunProductionMigrationAsync();
        using var handler = new MappingHandler();
        using var client = Client(handler);
        const string rawRule = " { \"mode\" : \"All\", \"conditions\" : null } ";
        const string rawSort = " { \"sort\" : \"legacy\" } ";
        var legacy = JsonSerializer.Serialize(new
        {
            schemaVersion = 5,
            tracking = new[] { new { animeId = 1, status = AnimeTrackingStatus.Watching, updatedAt = At } },
            p4Tables = new Dictionary<string, object>
            {
                ["smart_lists"] = new[]
                {
                    new Dictionary<string, string?>
                    {
                        ["Id"] = "rule",
                        ["Name"] = "保留旧规则",
                        ["SchemaVersion"] = "1",
                        ["RuleJson"] = rawRule,
                        ["SortJson"] = rawSort,
                        ["CreatedAt"] = At,
                        ["UpdatedAt"] = At,
                    },
                },
            },
        });
        var exporter = Export(client);

        var result = await exporter.ImportAsync(legacy);

        Assert.Equal(1, result.trackingCount);
        Assert.Equal(AnimeTrackingStatus.Watching,
            await new TrackingService(DbFactory).GetStatusAsync(101));
        Assert.Equal(rawRule, await ReadLegacyRulesAsync(DbFactory));
        var exported = ExportService.Preview(await exporter.ExportAsync())!;
        var rule = Assert.Single(exported.P4Tables!["smart_lists"]);
        Assert.Equal("保留旧规则", rule["Name"]);
        Assert.Equal(rawRule, rule["RuleJson"]);
        Assert.Equal(rawSort, rule["SortJson"]);

        var target = CreateAdditionalPaths();
        var factory = new SqliteConnectionFactory(target);
        await new DatabaseService(factory, target).InitializeAsync();
        var importer = new ExportService(factory);
        await importer.ImportAsync(JsonSerializer.Serialize(exported, PublicIdMigrationService.JsonOptions));

        Assert.Equal(rawRule, await ReadLegacyRulesAsync(factory));
        Assert.Equal(AnimeTrackingStatus.Watching,
            await new TrackingService(factory).GetStatusAsync(101));
        var roundTrip = ExportService.Preview(await importer.ExportAsync())!;
        Assert.Equal(rule, Assert.Single(roundTrip.P4Tables!["smart_lists"]));
    }
}
