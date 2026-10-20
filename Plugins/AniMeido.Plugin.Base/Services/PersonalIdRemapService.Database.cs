using AniMeido.Plugin.Base.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace AniMeido.Plugin.Base.Services;

public sealed partial class PersonalIdRemapService
{
    internal static async Task<PersonalIdRemapResult> RewriteAsync(SqliteConnection connection, SqliteTransaction tx,
        int from, int to, MigrationInput records, CancellationToken ct)
    {
        var changes = PublicIdMigrationService.WorkTables.ToDictionary(t => t, _ => (Moved: 0, Discarded: 0));
        var sourceTitle = PendingReviewService.Title(records, from);
        await KeepNewer("tracking", "UpdatedAt").ConfigureAwait(false);
        await KeepNewer("anime_progress", "LastWatchedAt").ConfigureAwait(false);
        await KeepNewer("episode_progress", "LastWatchedAt", "EpisodeNumber").ConfigureAwait(false);

        var sourcePlan = Row("anime_plans", from);
        var targetPlan = Row("anime_plans", to);
        if (sourcePlan is not null)
        {
            if (targetPlan is null || IsNewer(sourcePlan["UpdatedAt"], targetPlan["UpdatedAt"]))
            {
                if (targetPlan is not null) await DropPlan(to).ConfigureAwait(false);
                await Move("anime_plans").ConfigureAwait(false);
                await Move("plan_reminders").ConfigureAwait(false);
            }
            else await DropPlan(from).ConfigureAwait(false);
        }

        var browse = Row("browse_history", from);
        var targetBrowse = Row("browse_history", to);
        if (browse is not null && targetBrowse is not null)
        {
            var count = long.Parse(browse["ViewCount"]!, CultureInfo.InvariantCulture)
                + long.Parse(targetBrowse["ViewCount"]!, CultureInfo.InvariantCulture);
            var at = IsNewer(browse["LastViewedAt"], targetBrowse["LastViewedAt"]) ? browse["LastViewedAt"] : targetBrowse["LastViewedAt"];
            await Execute("UPDATE browse_history SET ViewCount=@count,LastViewedAt=@at WHERE AnimeId=@to",
                ("@count", count), ("@at", at)).ConfigureAwait(false);
            await Execute("DELETE FROM browse_history WHERE AnimeId=@from").ConfigureAwait(false);
            Add("browse_history", 1, 0);
        }
        else await Move("browse_history").ConfigureAwait(false);

        var archive = Row("anime_archives", from);
        var targetArchive = Row("anime_archives", to);
        if (archive is not null && targetArchive is not null)
        {
            var note = targetArchive["SummaryNote"] ?? "";
            var sourceNote = archive["SummaryNote"] ?? "";
            if (!string.IsNullOrWhiteSpace(sourceNote) && sourceNote != note)
                note = note + (note.Length > 0 ? "\n" : "") + $"——并入自〈{sourceTitle}〉\n" + sourceNote;
            await Execute("UPDATE anime_archives SET PersonalRating=@rating,SummaryNote=@note WHERE AnimeId=@to",
                ("@rating", targetArchive["PersonalRating"] ?? archive["PersonalRating"]), ("@note", note)).ConfigureAwait(false);
            Add("anime_archives", 1, 0);
        }
        else await Move("anime_archives").ConfigureAwait(false);

        var tagCount = records.Rows("anime_personal_tags").Count(r => MigrationInput.Id(r["AnimeId"]) == from);
        var inserted = await Execute("INSERT OR IGNORE INTO anime_personal_tags(AnimeId,TagId) SELECT @to,TagId FROM anime_personal_tags WHERE AnimeId=@from")
            .ConfigureAwait(false);
        await Execute("DELETE FROM anime_personal_tags WHERE AnimeId=@from").ConfigureAwait(false);
        Add("anime_personal_tags", inserted, tagCount - inserted);
        foreach (var table in new[] { "tracking_events", "watch_sessions", "manual_watch_events", "screenshots", "archive_entries" })
            await Move(table).ConfigureAwait(false);
        if (archive is not null && targetArchive is not null)
            await Execute("DELETE FROM anime_archives WHERE AnimeId=@from").ConfigureAwait(false);

        if (Row("recommendation_hidden_anime", to) is null) await Move("recommendation_hidden_anime").ConfigureAwait(false);
        else Add("recommendation_hidden_anime", 0,
            await Execute("DELETE FROM recommendation_hidden_anime WHERE AnimeId=@from").ConfigureAwait(false));

        await Execute("DELETE FROM legacy_anime_refs WHERE LocalId=@from; DELETE FROM public_id_issues WHERE AnimeId=@from;").ConfigureAwait(false);
        // 推荐与详情缓存包含旧身份；只清可重建资料，不改观看事实和其它配置。
        await Execute("DELETE FROM cache").ConfigureAwait(false);
        await AppendLogAsync(connection, tx, from, to, ct).ConfigureAwait(false);
        return new PersonalIdRemapResult(from, to, changes.Select(p => new RemapTableChange(p.Key, p.Value.Moved, p.Value.Discarded)).ToList());

        Dictionary<string, string?>? Row(string table, int id) => records.Rows(table)
            .FirstOrDefault(r => MigrationInput.Id(r["AnimeId"]) == id);

        void Add(string table, int moved, int discarded)
        {
            var old = changes[table]; changes[table] = (old.Moved + moved, old.Discarded + discarded);
        }

        async Task Move(string table)
            => Add(table, await Execute($"UPDATE {table} SET AnimeId=@to WHERE AnimeId=@from").ConfigureAwait(false), 0);

        async Task DropPlan(int id)
        {
            Add("plan_reminders", 0, records.Rows("plan_reminders").Count(r => MigrationInput.Id(r["AnimeId"]) == id));
            Add("anime_plans", 0, await Execute("DELETE FROM anime_plans WHERE AnimeId=@id", ("@id", id)).ConfigureAwait(false));
        }

        async Task KeepNewer(string table, string time, string? key = null)
        {
            foreach (var source in records.Rows(table).Where(r => MigrationInput.Id(r["AnimeId"]) == from))
            {
                var target = records.Rows(table).FirstOrDefault(r => MigrationInput.Id(r["AnimeId"]) == to
                    && (key is null || r[key] == source[key]));
                var condition = key is null ? "" : $" AND {key}=@part";
                (string, object?)[] part = key is null ? [] : [("@part", source[key])];
                if (target is not null)
                {
                    var dropId = IsNewer(source[time], target[time]) ? to : from;
                    Add(table, 0, await Execute($"DELETE FROM {table} WHERE AnimeId=@id{condition}",
                        part.Append(("@id", (object?)dropId)).ToArray()).ConfigureAwait(false));
                    if (dropId == from) continue;
                }
                Add(table, await Execute($"UPDATE {table} SET AnimeId=@to WHERE AnimeId=@from{condition}", part).ConfigureAwait(false), 0);
            }
        }

        async Task<int> Execute(string sql, params (string Name, object? Value)[] args)
        {
            using var command = connection.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
            command.Parameters.AddWithValue("@from", from); command.Parameters.AddWithValue("@to", to);
            foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static bool IsNewer(string? source, string? target) =>
        DateTimeOffset.TryParse(source, CultureInfo.InvariantCulture, DateTimeStyles.None, out var a)
        && (!DateTimeOffset.TryParse(target, CultureInfo.InvariantCulture, DateTimeStyles.None, out var b) || a > b);
}
