using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AniMeido.Plugin.Base.Services;

public sealed partial class PublicIdMigrationService
{
    internal static async Task ApplyAsync(SqliteConnection connection, PublicIdMigrationPlan plan, CancellationToken ct = default)
    {
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON";
            await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        using var tx = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = "PRAGMA user_version";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) != DatabaseSchema.OfflineVersion)
            throw new InvalidOperationException("公共 ID 迁移仅适用于 v8 数据库。");
        if (plan.SourceState is not null && (await ReadInputAsync(connection, tx, ct).ConfigureAwait(false)).State() != plan.SourceState)
            throw new InvalidOperationException("联网准备期间个人记录发生变化，未执行迁移，请重试。");
        command.CommandText = """
            PRAGMA defer_foreign_keys = ON;
            CREATE TABLE legacy_anime_refs(
                LocalId INTEGER PRIMARY KEY, LegacyBangumiId INTEGER NOT NULL,
                Reason TEXT NOT NULL, TitleSnapshot TEXT, RecordedAt TEXT NOT NULL);
            ALTER TABLE recommendation_feature_preferences ADD COLUMN Invalid INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE saved_tags ADD COLUMN Invalid INTEGER NOT NULL DEFAULT 0;
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        // 临时 ID 位于 Int32 之外，既不碰旧 ID，也不碰最终公共/负数 ID。
        var temporary = plan.WorkIds.Keys.Order().Select((id, index) => (id, temp: long.MinValue + index)).ToList();
        foreach (var table in WorkTables)
        {
            foreach (var (id, temp) in temporary) await Rewrite(table, id, temp).ConfigureAwait(false);
            foreach (var (id, temp) in temporary) await Rewrite(table, temp, plan.WorkIds[id]).ConfigureAwait(false);
        }
        foreach (var reference in plan.LegacyRefs)
        {
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO legacy_anime_refs VALUES(@id,@old,@reason,@title,@at)";
            command.Parameters.AddWithValue("@id", reference.LocalId); command.Parameters.AddWithValue("@old", reference.LegacyBangumiId);
            command.Parameters.AddWithValue("@reason", reference.Reason); command.Parameters.AddWithValue("@title", reference.TitleSnapshot ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@at", reference.RecordedAt);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        command.Parameters.Clear(); command.CommandText = "DELETE FROM saved_tags; DELETE FROM recommendation_feature_preferences;";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        foreach (var tag in plan.Tags)
        {
            command.Parameters.Clear(); command.CommandText = "INSERT INTO saved_tags(TagName,Invalid) VALUES(@tag,@invalid)";
            command.Parameters.AddWithValue("@tag", tag.Name); command.Parameters.AddWithValue("@invalid", tag.Invalid ? 1 : 0);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        foreach (var preference in plan.Preferences)
        {
            command.Parameters.Clear(); command.CommandText = "INSERT INTO recommendation_feature_preferences VALUES(@kind,@key,@name,@adjustment,@at,@invalid)";
            command.Parameters.AddWithValue("@kind", preference.Kind); command.Parameters.AddWithValue("@key", preference.Key);
            command.Parameters.AddWithValue("@name", preference.DisplayName); command.Parameters.AddWithValue("@adjustment", preference.Adjustment);
            command.Parameters.AddWithValue("@at", preference.UpdatedAt); command.Parameters.AddWithValue("@invalid", preference.Invalid ? 1 : 0);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        foreach (var rule in plan.Rules)
        {
            command.Parameters.Clear(); command.CommandText = "UPDATE smart_lists SET RuleJson=@rule WHERE Id=@id";
            command.Parameters.AddWithValue("@rule", rule.Value); command.Parameters.AddWithValue("@id", rule.Key);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await SaveConfigAsync(command, PlayerMapConfigKey, JsonSerializer.Serialize(new PlayerIdMigrationFile(9, plan.WorkIds), JsonOptions), ct).ConfigureAwait(false);
        if (plan.HasVocabulary)
            await SaveConfigAsync(command, VocabConfigKey, JsonSerializer.Serialize(plan.VocabularyNames, JsonOptions), ct).ConfigureAwait(false);
        command.Parameters.Clear(); command.CommandText = "DELETE FROM cache; PRAGMA user_version = 9;";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        tx.Commit();

        async Task Rewrite(string table, long from, long to)
        {
            command.Parameters.Clear(); command.CommandText = $"UPDATE {table} SET AnimeId=@to WHERE AnimeId=@from";
            command.Parameters.AddWithValue("@to", to); command.Parameters.AddWithValue("@from", from);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task SaveConfigAsync(SqliteCommand command, string key, string json, CancellationToken ct)
    {
        command.Parameters.Clear(); command.CommandText = "INSERT INTO config(Key,Value) VALUES(@key,@value) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value";
        command.Parameters.AddWithValue("@key", key); command.Parameters.AddWithValue("@value", json);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // 对照表与 v9 同事务持久化；提交后写文件失败/进程退出，重试可从这里恢复，不重跑 ID 改写。
    private async Task WritePlayerMapAsync(CancellationToken ct)
    {
        using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand(); command.CommandText = "SELECT Value FROM config WHERE Key=@key";
        command.Parameters.AddWithValue("@key", PlayerMapConfigKey);
        if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string json) return;
        var path = Path.Combine(Path.GetDirectoryName(_db.DatabasePath)!, "id-migration-v9.json");
        if (File.Exists(path) && await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) == json) return;
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temp, json.ReplaceLineEndings("\r\n"), new UTF8Encoding(true), ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private sealed record PlayerIdMigrationFile(int SchemaVersion, Dictionary<int, int> WorkIds);
}
