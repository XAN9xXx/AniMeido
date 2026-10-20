using AniMeido.Plugin.Base.Models;
using System.Globalization;
using System.Text.Json;

namespace AniMeido.Plugin.Base.Services;

public sealed record LegacyAnimeReference(int LocalId, int LegacyBangumiId, string Reason,
    string? TitleSnapshot, string RecordedAt, string? KeptAt = null);

internal sealed record IdMapping(int SourceId, int? PublicId, string Status);
internal sealed record MigratedPreference(int Kind, string Key, string DisplayName, int Adjustment, string UpdatedAt, bool Invalid);
internal sealed record MigratedTag(string Name, bool Invalid);

internal sealed class MigrationInput
{
    internal Dictionary<string, List<Dictionary<string, string?>>> Tables { get; } = new(StringComparer.Ordinal);
    internal Dictionary<int, int> Counts { get; } = [];
    internal Dictionary<int, string> Titles { get; } = [];
    internal List<string> CacheJson { get; } = [];
    internal IEnumerable<Dictionary<string, string?>> Rows(string table) => Tables.GetValueOrDefault(table) ?? [];

    internal void CollectWorks()
    {
        foreach (var table in PublicIdMigrationService.WorkTables)
            foreach (var row in Rows(table))
                if (Id(row.GetValueOrDefault("AnimeId")) is > 0 and var id)
                    Counts[id] = Counts.GetValueOrDefault(id) + 1;
        foreach (var (table, column) in new[]
        {
            ("anime_archives", "TitleSnapshot"), ("anime_plans", "TitleSnapshot"),
            ("browse_history", "TitleSnapshot"), ("manual_watch_events", "TitleSnapshot"),
            ("recommendation_hidden_anime", "TitleSnapshot"), ("screenshots", "AnimeTitle"),
        })
            foreach (var row in Rows(table))
                AddTitle(Id(row.GetValueOrDefault("AnimeId")), row.GetValueOrDefault(column));
        foreach (var json in CacheJson)
        {
            try { using var doc = JsonDocument.Parse(json); ReadCachedTitles(doc.RootElement); }
            catch (JsonException) { /* 损坏/非 JSON 缓存可重建，不影响个人记录迁移。 */ }
        }
    }

    private void ReadCachedTitles(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) ReadCachedTitles(item);
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            var props = element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
            if (props.TryGetValue("id", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var id))
            {
                foreach (var name in new[] { "title", "name_cn", "name" })
                    if (props.TryGetValue(name, out var title) && title.ValueKind == JsonValueKind.String)
                        AddTitle(id, title.GetString());
            }
            foreach (var child in element.EnumerateObject())
                if (child.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) ReadCachedTitles(child.Value);
        }
    }

    private void AddTitle(int id, string? title)
    {
        if (id > 0 && !string.IsNullOrWhiteSpace(title)) Titles.TryAdd(id, title);
    }

    internal static int Id(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;

    internal string State() => JsonSerializer.Serialize(new
    {
        Tables = Tables.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => new
        {
            t.Key,
            Rows = t.Value.Select(row => JsonSerializer.Serialize(row.OrderBy(p => p.Key, StringComparer.Ordinal)))
                .Order(StringComparer.Ordinal).ToArray(),
        }).ToArray(),
        CacheJson,
    });
}

internal sealed class PublicIdMigrationPlan
{
    internal Dictionary<int, int> WorkIds { get; } = [];
    internal List<LegacyAnimeReference> LegacyRefs { get; } = [];
    internal List<MigratedTag> Tags { get; } = [];
    internal List<MigratedPreference> Preferences { get; } = [];
    internal Dictionary<string, string> Rules { get; } = [];
    internal HashSet<string> VocabularyNames { get; } = new(StringComparer.Ordinal);
    internal bool HasVocabulary { get; set; }
    internal string? SourceState { get; set; }

    internal static PublicIdMigrationPlan ResolveWorks(MigrationInput input, IReadOnlyList<IdMapping> mappings)
    {
        var plan = new PublicIdMigrationPlan();
        foreach (var group in mappings.Where(m => m.PublicId > 0).GroupBy(m => m.PublicId!.Value))
        {
            var ordered = group.OrderBy(m => m.Status == "active" ? 0 : 1)
                .ThenByDescending(m => input.Counts.GetValueOrDefault(m.SourceId)).ThenBy(m => m.SourceId).ToList();
            plan.WorkIds[ordered[0].SourceId] = group.Key;
            foreach (var loser in ordered.Skip(1)) Unresolved(loser.SourceId, "conflict");
        }
        foreach (var mapping in mappings.Where(m => m.PublicId is null))
            Unresolved(mapping.SourceId, mapping.Status == "deactivated" ? "deactivated" : "not_found");
        return plan;

        void Unresolved(int id, string reason)
        {
            plan.WorkIds[id] = -id;
            plan.LegacyRefs.Add(new LegacyAnimeReference(-id, id, reason,
                input.Titles.GetValueOrDefault(id) ?? $"Bangumi 条目 #{id}", DateTimeOffset.UtcNow.ToString("O")));
        }
    }
}
