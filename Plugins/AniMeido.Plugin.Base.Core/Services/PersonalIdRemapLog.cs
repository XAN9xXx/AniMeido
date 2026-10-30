using System.Text.Json;
using AniMeido.Plugin.Base.Models;
using Microsoft.Data.Sqlite;

namespace AniMeido.Plugin.Base.Services;

/// <summary>借用当前业务事务保存改绑历史，不单独提交或持有跨事务状态。</summary>
internal sealed class PersonalIdRemapLog(SqliteConnection connection, SqliteTransaction transaction)
{
    private List<IdRemapEntry>? _entries;

    internal async Task AppendAsync(int from, int to, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _entries ??= await ReadAsync(ct).ConfigureAwait(false);
        _entries.Add(new IdRemapEntry(
            checked((_entries.LastOrDefault()?.Seq ?? 0) + 1),
            from,
            to,
            DateTimeOffset.UtcNow.ToString("O")));
    }

    // 提交入口负责保存，改写调用方只追加，不自行协调保存顺序。
    internal async Task SaveAsync(CancellationToken ct)
    {
        if (_entries is null)
            return;

        ct.ThrowIfCancellationRequested();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO config(Key,Value) VALUES(@key,@value)
            ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value
            """;
        command.Parameters.AddWithValue("@key", PersonalIdRemapService.LogConfigKey);
        command.Parameters.AddWithValue("@value", JsonSerializer.Serialize(_entries, PublicIdMigrationService.JsonOptions));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<List<IdRemapEntry>> ReadAsync(CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Value FROM config WHERE Key=@key";
        command.Parameters.AddWithValue("@key", PersonalIdRemapService.LogConfigKey);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is string json
            ? JsonSerializer.Deserialize<List<IdRemapEntry>>(json, PublicIdMigrationService.JsonOptions)!
            : [];
    }
}
