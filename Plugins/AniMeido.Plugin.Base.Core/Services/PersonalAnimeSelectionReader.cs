using AniMeido.Contracts.Models;
using AniMeido.Contracts.PersonalAnime;
using AniMeido.Plugin.Base.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace AniMeido.Plugin.Base.Services;

internal static class PersonalAnimeSelectionReader
{
    internal const int MaximumSelectionLimit = 200;

    internal static Task<IReadOnlyList<PersonalAnimeSelectionItem>> ReadAsync(
        SqliteConnectionFactory factory,
        PersonalAnimeSelectionQuery query,
        CancellationToken cancellationToken)
        => Task.Run(async () => Select(
            await ReadCandidatesAsync(factory, cancellationToken).ConfigureAwait(false),
            query, cancellationToken), cancellationToken);

    private static async Task<List<Candidate>> ReadCandidatesAsync(
        SqliteConnectionFactory factory,
        CancellationToken cancellationToken)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var byId = new Dictionary<int, Candidate>();
        var candidates = new List<Candidate>();

        await ReadRowsAsync("""
            SELECT AnimeId, Status, UpdatedAt
            FROM tracking
            ORDER BY UpdatedAt DESC
            """, reader =>
        {
            var candidate = GetCandidate(reader.GetInt32(0));
            candidate.Status = (AnimeTrackingStatus)reader.GetInt32(1);
            candidate.TrackingUpdatedAt = reader.IsDBNull(2) ? null : reader.GetString(2);
        }).ConfigureAwait(false);

        await ReadRowsAsync("""
            SELECT AnimeId, TitleSnapshot, UpdatedAt
            FROM anime_plans
            WHERE ArchivedAt IS NULL
            ORDER BY Priority DESC,
                     CASE WHEN TargetStartDate IS NULL THEN 1 ELSE 0 END,
                     TargetStartDate,
                     SortOrder,
                     UpdatedAt DESC
            """, reader =>
        {
            var candidate = GetCandidate(reader.GetInt32(0));
            candidate.Title = reader.GetString(1);
            candidate.PlanUpdatedAt = ParseTimestamp(reader.GetString(2));
        }).ConfigureAwait(false);

        await ReadRowsAsync("""
            SELECT AnimeId, TitleSnapshot, PersonalRating, UpdatedAt
            FROM anime_archives
            ORDER BY UpdatedAt DESC
            """, reader =>
        {
            var candidate = GetCandidate(reader.GetInt32(0));
            candidate.Title = reader.GetString(1);
            candidate.PersonalRating = reader.IsDBNull(2) ? null : reader.GetDouble(2);
            candidate.ArchiveUpdatedAt = ParseTimestamp(reader.GetString(3));
        }).ConfigureAwait(false);

        await ReadRowsAsync($"""
            SELECT AnimeId, TitleSnapshot, LastViewedAt
            FROM browse_history
            ORDER BY LastViewedAt DESC
            LIMIT {MaximumSelectionLimit}
            """, reader =>
        {
            var candidate = GetCandidate(reader.GetInt32(0));
            candidate.Title ??= reader.IsDBNull(1) ? null : reader.GetString(1);
            candidate.BrowseUpdatedAt = new DateTimeOffset(DateTime.Parse(reader.GetString(2)));
        }).ConfigureAwait(false);

        transaction.Commit();
        return candidates;

        Candidate GetCandidate(int animeId)
        {
            if (byId.TryGetValue(animeId, out var candidate))
            {
                return candidate;
            }

            candidate = new Candidate(animeId);
            byId.Add(animeId, candidate);
            candidates.Add(candidate);
            return candidate;
        }

        async Task ReadRowsAsync(string sql, Action<SqliteDataReader> collect)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                collect(reader);
            }
        }
    }

    private static IReadOnlyList<PersonalAnimeSelectionItem> Select(
        List<Candidate> candidates,
        PersonalAnimeSelectionQuery query,
        CancellationToken cancellationToken)
    {
        var statuses = query.TrackingStatuses?.ToHashSet();
        var search = query.SearchText?.Trim();
        var limit = Math.Clamp(query.Limit, 1, MaximumSelectionLimit);
        var selected = new List<PersonalAnimeSelectionItem>();

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (query.PlansOnly && candidate.PlanUpdatedAt is null
                || query.ArchivesOnly && candidate.ArchiveUpdatedAt is null
                || statuses is { Count: > 0 }
                    && (candidate.Status is null || !statuses.Contains(candidate.Status.Value)))
            {
                continue;
            }

            var item = candidate.ToSelection();
            if (!string.IsNullOrWhiteSpace(search)
                && !item.Title.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            selected.Add(item);
        }

        return selected
            .OrderByDescending(item => item.UpdatedAt)
            .Take(limit)
            .ToArray();
    }

    private static DateTimeOffset ParseTimestamp(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed class Candidate(int animeId)
    {
        internal string? Title { get; set; }
        internal AnimeTrackingStatus? Status { get; set; }
        internal string? TrackingUpdatedAt { get; set; }
        internal DateTimeOffset? PlanUpdatedAt { get; set; }
        internal DateTimeOffset? ArchiveUpdatedAt { get; set; }
        internal DateTimeOffset? BrowseUpdatedAt { get; set; }
        internal double? PersonalRating { get; set; }

        internal PersonalAnimeSelectionItem ToSelection()
        {
            var updatedAt = DateTimeOffset.TryParse(TrackingUpdatedAt,
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var trackedAt)
                ? trackedAt
                : PlanUpdatedAt ?? ArchiveUpdatedAt ?? BrowseUpdatedAt ?? DateTimeOffset.MinValue;

            return new PersonalAnimeSelectionItem(
                animeId,
                Title ?? AnimeTitleFallback.Untitled,
                Status,
                PlanUpdatedAt is not null,
                ArchiveUpdatedAt is not null,
                PersonalRating,
                updatedAt);
        }
    }
}
