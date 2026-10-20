using AniMeido.Plugin.Base.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace AniMeido.Plugin.Base.Services;

public sealed partial class PersonalIdRemapService(SqliteConnectionFactory db, ILogger<PersonalIdRemapService> logger,
    CacheService? cache = null)
{
    internal const string LogConfigKey = "public-id-remap-log";
    private readonly SemaphoreSlim _gate = new(1, 1);
    public event Action? Changed;

    public async Task RebindAsync(int from, int to, bool allowMerge, CancellationToken ct = default)
    {
        if (from == 0 || to == 0 || from == to) throw new ArgumentException("来源与目标必须是不同的非零作品 ID。");
        PersonalIdRemapResult? result = null;
        await RunTransactionAsync(async (connection, tx) =>
        {
            using var exists = connection.CreateCommand(); exists.Transaction = tx;
            exists.CommandText = "SELECT COUNT(*) FROM legacy_anime_refs WHERE LocalId=@id UNION ALL SELECT COUNT(*) FROM public_id_issues WHERE AnimeId=@id";
            exists.Parameters.AddWithValue("@id", from);
            using var reader = await exists.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var pending = false;
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) pending |= reader.GetInt32(0) > 0;
            if (!pending) throw new InvalidOperationException("该待确认项已被处理，请刷新后重试。");
            await reader.DisposeAsync();
            var records = await PendingReviewService.ReadRecordsAsync(connection, tx, ct).ConfigureAwait(false);
            if (!allowMerge && PendingReviewService.AllIds(records).Contains(to))
                throw new InvalidOperationException("所选作品已有记录，将合并；请确认后重试。");
            if (to < 0 && !PendingReviewService.AllIds(records).Contains(to))
                throw new InvalidOperationException("只能并入已有的本地负数作品。");
            result = await RewriteAsync(connection, tx, from, to, records, ct).ConfigureAwait(false);
        }, ct, invalidateCache: true).ConfigureAwait(false);
        if (result is not null) Log(result);
    }

    internal async Task RunTransactionAsync(Func<SqliteConnection, SqliteTransaction, Task> action, CancellationToken ct,
        bool invalidateCache = false)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA foreign_keys=ON"; await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            using (var tx = connection.BeginTransaction())
            {
                using var command = connection.CreateCommand(); command.Transaction = tx;
                command.CommandText = "PRAGMA defer_foreign_keys=ON"; await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                await action(connection, tx).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                tx.Commit();
            }
            if (invalidateCache && cache is not null)
            {
                try { await cache.ClearMetadataForIdRemapAsync().ConfigureAwait(false); }
                catch (Exception ex) when (ex is SqliteException or IOException)
                { logger.LogWarning(ex, "Committed ID remap cache invalidation failed"); }
            }
            // 提交后记录已生效，调用方取消不能中断日志发布和 Changed 通知。
            await FlushLogSafelyAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Personal ID transaction failed");
            throw;
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }

    internal void Log(PersonalIdRemapResult result) => logger.LogInformation(
        "Rewrote personal records {From} -> {To}; table moved/discarded rows: {Changes}",
        result.From, result.To, string.Join("; ", result.Changes.Select(c => $"{c.Table}={c.Moved}/{c.Discarded}")));

    internal static async Task AppendLogAsync(SqliteConnection connection, SqliteTransaction tx, int from, int to, CancellationToken ct)
    {
        using var command = connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT Value FROM config WHERE Key=@key"; command.Parameters.AddWithValue("@key", LogConfigKey);
        var rows = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is string json
            ? JsonSerializer.Deserialize<List<IdRemapEntry>>(json, PublicIdMigrationService.JsonOptions)! : [];
        rows.Add(new IdRemapEntry(checked((rows.LastOrDefault()?.Seq ?? 0) + 1), from, to, DateTimeOffset.UtcNow.ToString("O")));
        command.CommandText = "INSERT INTO config(Key,Value) VALUES(@key,@value) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value";
        command.Parameters.AddWithValue("@value", JsonSerializer.Serialize(rows, PublicIdMigrationService.JsonOptions));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task FlushLogAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await FlushLogSafelyAsync(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task FlushLogSafelyAsync(CancellationToken ct)
    {
        try
        {
            using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
            using var command = connection.CreateCommand(); command.CommandText = "SELECT Value FROM config WHERE Key=@key";
            command.Parameters.AddWithValue("@key", LogConfigKey);
            if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string json) return;
            var path = Path.Combine(Path.GetDirectoryName(db.DatabasePath)!, "id-remap-log.json");
            var temp = path + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(temp, json, new UTF8Encoding(true), ct).ConfigureAwait(false);
                File.Move(temp, path, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 与记录改写同事务保存日志；文件发布失败不能使用户把已成功的合并再做一遍。
            logger.LogWarning(ex, "Player ID remap log publication failed; retrying at next startup or write");
        }
    }
}
