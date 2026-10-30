using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class MetadataTagMigrationTests
{
    public static TheoryData<string> UntraversableRules => new()
    {
        " { \"mode\" : \"All\", \"conditions\" : null } ",
        " { \"mode\" : \"All\" } ",
        " { \"mode\" : \"All\", \"conditions\" : [null] } ",
        " { \"mode\" : \"All\", \"conditions\" : [], \"groups\" : [null] } ",
        " { \"mode\" : \"All\", \"conditions\" : [], \"groups\" : [{\"mode\":\"Any\",\"conditions\":null}] } ",
    };

    [Theory]
    [MemberData(nameof(UntraversableRules))]
    public void UntraversableLegacyRule_HasNoMigratableTagNames(string json)
    {
        Assert.Empty(MetadataTagMigration.TagNames(json));
    }

    [Theory]
    [MemberData(nameof(UntraversableRules))]
    public void UntraversableLegacyRule_PreservesTheOriginalJsonVerbatim(string json)
    {
        var migration = CreateMigration();

        Assert.Equal(json, migration.ConvertRules(json));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    public void ExistingUnreadableRuleFallback_RemainsUnchanged(string json)
    {
        Assert.Empty(MetadataTagMigration.TagNames(json));
        Assert.Equal(json, CreateMigration().ConvertRules(json));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidLegacyRule_WithMissingOrNullGroupsStillMigratesTags(bool explicitNullGroups)
    {
        var groups = explicitNullGroups ? ",\"groups\":null" : "";
        var json = "{\"mode\":\"Any\",\"conditions\":["
            + "{\"field\":\"Title\",\"operator\":\"Equals\",\"value\":\"保留标题\"},"
            + "{\"field\":\"Tags\",\"operator\":\"ContainsAll\",\"value\":\"漫画改,未知\"}]"
            + groups + "}";

        var converted = CreateMigration().ConvertRules(json);
        var group = JsonSerializer.Deserialize<SmartListRuleGroup>(
            converted, PublicIdMigrationService.JsonOptions)!;

        Assert.Equal(["漫画改", "未知"], MetadataTagMigration.TagNames(json));
        Assert.Equal(["漫画改编", "未知"], MetadataTagMigration.TagNames(converted));
        Assert.Equal(SmartListGroupMode.Any, group.Mode);
        Assert.Equal(new SmartListCondition(SmartListField.Title, SmartListOperator.Equals, "保留标题"),
            group.Conditions[0]);
        Assert.Equal(new SmartListCondition(SmartListField.Tags, SmartListOperator.ContainsAll, "漫画改编,未知"),
            group.Conditions[1]);
        Assert.Null(group.Groups);
    }

    [Fact]
    public void ValidNestedLegacyRule_StillMigratesItsTagNames()
    {
        const string json = """
            {"mode":"All","conditions":[],"groups":[
              {"mode":"Any","conditions":[{"field":"Tags","operator":"ContainsAny","value":"漫画改"}]}
            ]}
            """;

        var converted = CreateMigration().ConvertRules(json);

        Assert.Equal(["漫画改"], MetadataTagMigration.TagNames(json));
        Assert.Equal(["漫画改编"], MetadataTagMigration.TagNames(converted));
    }

    private static MetadataTagMigration CreateMigration()
        => MetadataTagMigration.Load([new MetadataVocab("source_type", "manga", "漫画改编")]);
}
