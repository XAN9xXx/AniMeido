using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AniMeido.Plugin.Base.Services;

internal static class MetadataTagAvailability
{
    internal static async Task<HashSet<string>?> ReadNamesAsync(
        SqliteConnectionFactory db,
        CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        return await ReadNamesAsync(connection, null, ct).ConfigureAwait(false);
    }

    internal static async Task<HashSet<string>?> ReadNamesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT Value FROM config WHERE Key=@key";
        cmd.Parameters.AddWithValue("@key", PublicIdMigrationService.VocabConfigKey);
        var snapshot = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is string json
            ? JsonSerializer.Deserialize<HashSet<string>>(json, PublicIdMigrationService.JsonOptions)
            : null;

        cmd.Parameters.Clear();
        cmd.CommandText = """
            SELECT Data FROM cache WHERE CacheKey IN (
                'metadata:vocab:tag:data:' || (SELECT Data FROM cache WHERE CacheKey='metadata:data-version'),
                'metadata:vocab:format:data:' || (SELECT Data FROM cache WHERE CacheKey='metadata:data-version'),
                'metadata:vocab:country:data:' || (SELECT Data FROM cache WHERE CacheKey='metadata:data-version'),
                'metadata:vocab:source_type:data:' || (SELECT Data FROM cache WHERE CacheKey='metadata:data-version'))
            ORDER BY CacheKey
            """;
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var completePages = 0;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var page = JsonSerializer.Deserialize<MetadataPage<MetadataVocab>>(
                    reader.GetString(0),
                    PublicIdMigrationService.JsonOptions);
                if (page is { Items.Count: > 0 })
                {
                    completePages++;
                    names.UnionWith(page.Items.Select(v => v.NameZh));
                }
            }
            catch (JsonException)
            {
                // 损坏缓存由资料数据源清理，规则读取保持只读。
            }
        }
        // 只有同一版本的四份完整词表才能替换迁移快照；staff_role 或半套缓存不能误判标签失效。
        return completePages == 4 ? names : snapshot;
    }
}
