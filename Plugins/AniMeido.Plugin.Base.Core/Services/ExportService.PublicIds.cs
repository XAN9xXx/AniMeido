using AniMeido.Plugin.Base.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace AniMeido.Plugin.Base.Services;

public partial class ExportService
{
    internal sealed record PreparedImport(ExportData Data, IReadOnlyDictionary<int, int> WorkIds);

    internal async Task<PreparedImport> PrepareImportAsync(string json, IEnumerable<AnimeScreenshot>? screenshots = null,
        CancellationToken ct = default)
    {
        var export = ParseAndValidate(json);
        if (export.SchemaVersion == SchemaVersion) return new PreparedImport(export, new Dictionary<int, int>());
        var input = new MigrationInput();
        input.Tables["tracking"] = export.Tracking.Select(t => new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        { ["AnimeId"] = t.AnimeId.ToString(CultureInfo.InvariantCulture) }).ToList();
        foreach (var table in export.P4Tables ?? [])
            input.Tables[table.Key] = table.Value.Select(r => new Dictionary<string, string?>(r, StringComparer.OrdinalIgnoreCase)).ToList();
        input.Tables["saved_tags"] = (export.SavedTags ?? []).Select(t => new Dictionary<string, string?> { ["TagName"] = t.TagName }).ToList();
        if (screenshots is not null)
            input.Tables["screenshots"] = screenshots.Where(s => s.AnimeId is > 0).Select(s => new Dictionary<string, string?>
            { ["AnimeId"] = s.AnimeId!.Value.ToString(CultureInfo.InvariantCulture), ["AnimeTitle"] = s.AnimeTitle }).ToList();
        input.CollectWorks();
        var hasRecords = input.Tables.Values.Any(r => r.Count > 0);
        var plan = hasRecords ? await (_migration ?? throw new InvalidOperationException("旧格式导入需要联网资料换算服务。"))
            .PrepareAsync(input, ct).ConfigureAwait(false) : new PublicIdMigrationPlan();
        foreach (var row in export.Tracking)
            if (row.AnimeId > 0) row.AnimeId = plan.WorkIds[row.AnimeId];
        foreach (var table in export.P4Tables ?? [])
        {
            foreach (var row in table.Value)
            {
                if (PublicIdMigrationService.WorkTables.Contains(table.Key) && row.TryGetValue("AnimeId", out var old)
                    && MigrationInput.Id(old) is > 0 and var id)
                    row["AnimeId"] = plan.WorkIds[id].ToString(CultureInfo.InvariantCulture);
                if (table.Key == "smart_lists" && row.TryGetValue("Id", out var key) && key is not null)
                    row["RuleJson"] = plan.Rules[key];
            }
        }
        if (export.P4Tables?.ContainsKey("recommendation_feature_preferences") == true)
            export.P4Tables["recommendation_feature_preferences"] = plan.Preferences.Select(p => new Dictionary<string, string?>
            {
                ["FeatureKind"] = p.Kind.ToString(CultureInfo.InvariantCulture), ["FeatureKey"] = p.Key,
                ["DisplayName"] = p.DisplayName, ["Adjustment"] = p.Adjustment.ToString(CultureInfo.InvariantCulture),
                ["UpdatedAt"] = p.UpdatedAt, ["Invalid"] = p.Invalid ? "1" : "0",
            }).ToList();
        export.SavedTags = plan.Tags.Select(t => new SavedTagEntry { TagName = t.Name, Invalid = t.Invalid }).ToList();
        export.LegacyAnimeRefs = plan.LegacyRefs;
        export.SchemaVersion = SchemaVersion;
        export.IdSpace = "animeido-public";
        export.VocabularyNames = plan.HasVocabulary ? plan.VocabularyNames.ToList() : null;
        return new PreparedImport(export, plan.WorkIds);
    }

    private static async Task<List<LegacyAnimeReference>> ExportLegacyRefsAsync(
        SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT LocalId,LegacyBangumiId,Reason,TitleSnapshot,RecordedAt,KeptAt FROM legacy_anime_refs ORDER BY LocalId";
        using var reader = await command.ExecuteReaderAsync();
        var refs = new List<LegacyAnimeReference>();
        while (await reader.ReadAsync())
            refs.Add(new LegacyAnimeReference(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        return refs;
    }

    private static async Task ImportLegacyRefsAsync(SqliteConnection connection, SqliteTransaction tx, ExportData data)
    {
        foreach (var item in data.LegacyAnimeRefs ?? [])
        {
            using var command = connection.CreateCommand(); command.Transaction = tx;
            command.CommandText = """
                INSERT INTO legacy_anime_refs(LocalId,LegacyBangumiId,Reason,TitleSnapshot,RecordedAt,KeptAt)
                VALUES(@id,@old,@reason,@title,@at,@kept)
                ON CONFLICT(LocalId) DO UPDATE SET LegacyBangumiId=excluded.LegacyBangumiId,
                    Reason=excluded.Reason,TitleSnapshot=excluded.TitleSnapshot,RecordedAt=excluded.RecordedAt,KeptAt=excluded.KeptAt
                """;
            command.Parameters.AddWithValue("@id", item.LocalId); command.Parameters.AddWithValue("@old", item.LegacyBangumiId);
            command.Parameters.AddWithValue("@reason", item.Reason); command.Parameters.AddWithValue("@title", item.TitleSnapshot ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@at", item.RecordedAt);
            command.Parameters.AddWithValue("@kept", item.KeptAt ?? (object)DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }
        if (data.VocabularyNames is not null)
        {
            using var command = connection.CreateCommand(); command.Transaction = tx;
            command.CommandText = "INSERT INTO config(Key,Value) VALUES(@key,@value) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value";
            command.Parameters.AddWithValue("@key", PublicIdMigrationService.VocabConfigKey);
            command.Parameters.AddWithValue("@value", JsonSerializer.Serialize(data.VocabularyNames, JsonOptions));
            await command.ExecuteNonQueryAsync();
        }
    }
}
