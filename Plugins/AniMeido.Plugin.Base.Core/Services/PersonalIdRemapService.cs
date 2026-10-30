using AniMeido.Plugin.Base.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Text;

namespace AniMeido.Plugin.Base.Services;

public sealed partial class PersonalIdRemapService(
    SqliteConnectionFactory db,
    ILogger<PersonalIdRemapService> logger,
    CacheService? cache = null)
{
    internal const string LogConfigKey = "public-id-remap-log";
    private readonly SemaphoreSlim _gate = new(1, 1);
    public event Action? Changed;

    public async Task RebindAsync(int from, int to, bool allowMerge, CancellationToken ct = default)
    {
        if (from == 0 || to == 0 || from == to)
            throw new ArgumentException("来源与目标必须是不同的非零作品 ID。");

        PersonalIdRemapResult? result = null;
        await CommitIdentityChangesAsync(async (connection, transaction, log) =>
        {
            if (!await HasPendingReviewAsync(connection, transaction, from, ct).ConfigureAwait(false))
                throw new InvalidOperationException("该待确认项已被处理，请刷新后重试。");

            var records = await PersonalRecordReader.ReadSnapshotAsync(connection, transaction, ct)
                .ConfigureAwait(false);
            var targetExists = PersonalRecordReader.GetAnimeIds(records).Contains(to);
            if (!allowMerge && targetExists)
                throw new InvalidOperationException("所选作品已有记录，将合并；请确认后重试。");
            if (to < 0 && !targetExists)
                throw new InvalidOperationException("只能并入已有的本地负数作品。");

            result = await RewriteAsync(connection, transaction, from, to, records, log, ct).ConfigureAwait(false);
        }, ct, invalidateCache: true).ConfigureAwait(false);

        if (result is not null)
            Log(result);
    }

    /// <summary>提交身份变更，并负责缓存失效、日志发布和最终通知；不是通用 SQL 执行器。</summary>
    internal async Task CommitIdentityChangesAsync(
        Func<SqliteConnection, SqliteTransaction, PersonalIdRemapLog, Task> applyChanges,
        CancellationToken ct,
        bool invalidateCache = false)
    {
        using var operation = db.Access.BeginWrite();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(async () =>
            {
                using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "PRAGMA foreign_keys=ON";
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                using (var transaction = connection.BeginTransaction())
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "PRAGMA defer_foreign_keys=ON";
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    var log = new PersonalIdRemapLog(connection, transaction);
                    await applyChanges(connection, transaction, log).ConfigureAwait(false);
                    await log.SaveAsync(ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    transaction.Commit();
                }

                // 个人记录已经提交；后续取消不能中断日志发布和变更通知。
                if (invalidateCache && cache is not null)
                    await InvalidateCommittedCacheAsync().ConfigureAwait(false);
                await FlushLogSafelyAsync(CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        NotifyChanged();
    }

    private async Task InvalidateCommittedCacheAsync()
    {
        try
        {
            await cache!.ClearMetadataForIdRemapAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Committed ID remap cache invalidation failed");
        }
    }

    private static async Task<bool> HasPendingReviewAsync(
        SqliteConnection connection, SqliteTransaction transaction, int animeId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM legacy_anime_refs WHERE LocalId=@id)
                OR EXISTS(SELECT 1 FROM public_id_issues WHERE AnimeId=@id)
            """;
        command.Parameters.AddWithValue("@id", animeId);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    private void NotifyChanged()
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action)handler)();
            }
#pragma warning disable CA1031 // 数据已经提交；单个订阅者失败不能撤销成功或阻止其他订阅者刷新。
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Personal records committed but a change subscriber failed");
            }
#pragma warning restore CA1031
        }
    }

    internal void Log(PersonalIdRemapResult result) => logger.LogInformation(
        "Rewrote personal records {From} -> {To}; table moved/discarded rows: {Changes}",
        result.From, result.To, string.Join("; ", result.Changes.Select(c => $"{c.Table}={c.Moved}/{c.Discarded}")));

    public async Task FlushLogAsync(CancellationToken ct = default)
    {
        using var operation = db.Access.BeginWrite();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() => FlushLogSafelyAsync(ct)).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task FlushLogSafelyAsync(CancellationToken ct)
    {
        try
        {
            using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Value FROM config WHERE Key=@key";
            command.Parameters.AddWithValue("@key", LogConfigKey);
            if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string json)
                return;
            var path = Path.Combine(Path.GetDirectoryName(db.DatabasePath)!, "id-remap-log.json");
            var temp = path + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(temp, json, new UTF8Encoding(true), ct).ConfigureAwait(false);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            // 与记录改写同事务保存日志；文件发布失败不能使用户把已成功的合并再做一遍。
            logger.LogWarning(ex, "Player ID remap log publication failed; retrying at next startup or write");
        }
    }
}
