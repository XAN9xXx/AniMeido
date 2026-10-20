using AniMeido.Plugin.Base.Models;
using System.Text.Json;

namespace AniMeido.Plugin.Base.Services;

internal sealed class MetadataTagMigration
{
    private readonly Dictionary<string, string?> _names;
    private readonly HashSet<string> _current;
    private MetadataTagMigration(Dictionary<string, string?> names, HashSet<string> current) { _names = names; _current = current; }

    internal static MetadataTagMigration Load(IEnumerable<MetadataVocab> vocabulary)
    {
        var vocab = vocabulary.ToDictionary(v => (v.Category, v.Code), v => v.NameZh);
        using var stream = typeof(MetadataTagMigration).Assembly.GetManifestResourceStream("AniMeido.BangumiMetaTags")
            ?? throw new InvalidDataException("缺少内嵌的 Bangumi 元标签换算表。");
        using var doc = JsonDocument.Parse(stream);
        var names = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            var name = row.GetProperty("bangumi").GetString()!;
            names[name] = row.GetProperty("action").GetString() == "map"
                ? vocab.GetValueOrDefault((row.GetProperty("category").GetString()!, row.GetProperty("code").GetString()!)) : null;
        }
        return new MetadataTagMigration(names, vocab.Values.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    internal MigratedTag Convert(string name)
    {
        if (_names.TryGetValue(name, out var mapped)) return new MigratedTag(mapped ?? name, mapped is null);
        return new MigratedTag(name, !_current.Contains(name));
    }

    internal string ConvertRules(string json)
    {
        try
        {
            var group = JsonSerializer.Deserialize<SmartListRuleGroup>(json, PublicIdMigrationService.JsonOptions);
            return group is null ? json : JsonSerializer.Serialize(Rewrite(group), PublicIdMigrationService.JsonOptions);
        }
        catch (JsonException) { return json; }

        SmartListRuleGroup Rewrite(SmartListRuleGroup group) => group with
        {
            Conditions = group.Conditions.Select(c => c.Field == SmartListField.Tags && c.Value is not null
                ? c with { Value = string.Join(',', Split(c.Value).Select(name => Convert(name).Name)) } : c).ToList(),
            Groups = group.Groups?.Select(Rewrite).ToList(),
        };
    }

    internal static IEnumerable<string> TagNames(string json)
    {
        try
        {
            var group = JsonSerializer.Deserialize<SmartListRuleGroup>(json, PublicIdMigrationService.JsonOptions);
            return group is null ? [] : TagNames(group).ToList();
        }
        catch (JsonException) { return []; }
    }

    internal static IEnumerable<string> TagNames(SmartListRuleGroup group) =>
        group.Conditions.Where(c => c.Field == SmartListField.Tags && c.Value is not null).SelectMany(c => Split(c.Value!))
            .Concat(group.Groups?.SelectMany(TagNames) ?? []);

    private static IEnumerable<string> Split(string value) => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
