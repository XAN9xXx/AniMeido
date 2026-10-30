using AniMeido.Contracts.Models;
using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Base.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace AniMeido.Plugin.Base.Services;

internal sealed class PlaybackProgressService
{
    private readonly SqliteConnectionFactory _dbFactory;

    public PlaybackProgressService(SqliteConnectionFactory dbFactory)
        => _dbFactory = dbFactory;

    public async Task RecordAsync(
        AnimePlaybackProgress progress,
        CancellationToken cancellationToken = default)
    {
        using var operation = _dbFactory.Access.BeginWrite();
        ArgumentNullException.ThrowIfNull(progress);
        if (progress.AnimeId <= 0
            || progress.EpisodeNumber <= 0
            || progress.DurationSeconds < 300
            || progress.PositionSeconds < 0)
        {
            return;
        }

        var completed = progress.ReachedNaturalEnd
            || progress.PositionSeconds / progress.DurationSeconds >= 0.9;
        await Task.Run(async () =>
        {
            using var connection =
                await _dbFactory.OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            using (var session = connection.CreateCommand())
            {
                session.Transaction = transaction;
                session.CommandText = """
                    INSERT OR IGNORE INTO watch_sessions(
                        EventId, AnimeId, EpisodeNumber, PositionSeconds,
                        DurationSeconds, IsCompleted, ObservedAt)
                    VALUES(
                        @eventId, @animeId, @episode, @position,
                        @duration, @completed, @observedAt)
                    """;
                AddProgressParameters(session, progress, completed);
                var inserted = await session.ExecuteNonQueryAsync(
                    cancellationToken);
                if (inserted == 0)
                {
                    transaction.Rollback();
                    return;
                }
            }

            using (var episode = connection.CreateCommand())
            {
                episode.Transaction = transaction;
                episode.CommandText = """
                    INSERT INTO episode_progress(
                        AnimeId, EpisodeNumber, PositionSeconds, DurationSeconds,
                        IsCompleted, LastWatchedAt)
                    VALUES(
                        @animeId, @episode, @position, @duration,
                        @completed, @observedAt)
                    ON CONFLICT(AnimeId, EpisodeNumber) DO UPDATE SET
                        PositionSeconds = MAX(
                            episode_progress.PositionSeconds,
                            excluded.PositionSeconds),
                        DurationSeconds = MAX(
                            episode_progress.DurationSeconds,
                            excluded.DurationSeconds),
                        IsCompleted = MAX(
                            episode_progress.IsCompleted,
                            excluded.IsCompleted),
                        LastWatchedAt = MAX(
                            episode_progress.LastWatchedAt,
                            excluded.LastWatchedAt)
                    """;
                AddProgressParameters(episode, progress, completed);
                await episode.ExecuteNonQueryAsync(cancellationToken);
            }

            using (var snapshot = connection.CreateCommand())
            {
                snapshot.Transaction = transaction;
                snapshot.CommandText = """
                    INSERT INTO anime_progress(
                        AnimeId, CurrentEpisode, PositionSeconds,
                        DurationSeconds, LastWatchedAt)
                    VALUES(
                        @animeId, @episode, @position,
                        @duration, @observedAt)
                    ON CONFLICT(AnimeId) DO UPDATE SET
                        CurrentEpisode = MAX(
                            anime_progress.CurrentEpisode,
                            excluded.CurrentEpisode),
                        PositionSeconds = CASE
                            WHEN excluded.CurrentEpisode
                                > anime_progress.CurrentEpisode
                            THEN excluded.PositionSeconds
                            WHEN excluded.CurrentEpisode
                                = anime_progress.CurrentEpisode
                            THEN MAX(
                                anime_progress.PositionSeconds,
                                excluded.PositionSeconds)
                            ELSE anime_progress.PositionSeconds END,
                        DurationSeconds = CASE
                            WHEN excluded.CurrentEpisode
                                > anime_progress.CurrentEpisode
                            THEN excluded.DurationSeconds
                            WHEN excluded.CurrentEpisode
                                = anime_progress.CurrentEpisode
                            THEN MAX(
                                anime_progress.DurationSeconds,
                                excluded.DurationSeconds)
                            ELSE anime_progress.DurationSeconds END,
                        LastWatchedAt = MAX(
                            anime_progress.LastWatchedAt,
                            excluded.LastWatchedAt)
                    """;
                AddProgressParameters(snapshot, progress, completed);
                await snapshot.ExecuteNonQueryAsync(cancellationToken);
            }

            using (var tracking = connection.CreateCommand())
            {
                tracking.Transaction = transaction;
                tracking.CommandText = """
                    INSERT INTO tracking(AnimeId, Status, UpdatedAt)
                    VALUES(@animeId, @watching, @observedAt)
                    ON CONFLICT(AnimeId) DO UPDATE SET
                        Status = CASE
                            WHEN tracking.Status = @following
                            THEN @watching
                            ELSE tracking.Status END,
                        UpdatedAt = CASE
                            WHEN tracking.Status = @following
                            THEN @observedAt
                            ELSE tracking.UpdatedAt END
                    """;
                tracking.Parameters.AddWithValue(
                    "@animeId",
                    progress.AnimeId);
                tracking.Parameters.AddWithValue(
                    "@watching",
                    (int)AnimeTrackingStatus.Watching);
                tracking.Parameters.AddWithValue(
                    "@following",
                    (int)AnimeTrackingStatus.Following);
                tracking.Parameters.AddWithValue(
                    "@observedAt",
                    FormatTimestamp(progress.ObservedAt));
                await tracking.ExecuteNonQueryAsync(cancellationToken);
            }

            transaction.Commit();
        }).ConfigureAwait(false);
    }

    public Task<IReadOnlyDictionary<int, AnimeProgressSnapshot>> GetProgressAsync(
        CancellationToken cancellationToken = default)
        => Task.Run(() => ReadProgressAsync(cancellationToken), cancellationToken);

    private async Task<IReadOnlyDictionary<int, AnimeProgressSnapshot>> ReadProgressAsync(
        CancellationToken cancellationToken)
    {
        using var connection =
            await _dbFactory.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT AnimeId, CurrentEpisode, PositionSeconds,
                   DurationSeconds, LastWatchedAt
            FROM anime_progress
            """;
        var progress = new Dictionary<int, AnimeProgressSnapshot>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var snapshot = new AnimeProgressSnapshot(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetDouble(2),
                reader.GetDouble(3),
                ParseTimestamp(reader.GetString(4)));
            progress[snapshot.AnimeId] = snapshot;
        }

        return progress;
    }

    private static void AddProgressParameters(
        SqliteCommand command,
        AnimePlaybackProgress progress,
        bool completed)
    {
        command.Parameters.AddWithValue("@eventId", progress.EventId);
        command.Parameters.AddWithValue("@animeId", progress.AnimeId);
        command.Parameters.AddWithValue(
            "@episode",
            progress.EpisodeNumber);
        command.Parameters.AddWithValue(
            "@position",
            progress.PositionSeconds);
        command.Parameters.AddWithValue(
            "@duration",
            progress.DurationSeconds);
        command.Parameters.AddWithValue(
            "@completed",
            completed ? 1 : 0);
        command.Parameters.AddWithValue(
            "@observedAt",
            FormatTimestamp(progress.ObservedAt));
    }

    private static DateTimeOffset ParseTimestamp(string value)
        => DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private static string FormatTimestamp(DateTimeOffset value)
        => value.ToString("O", CultureInfo.InvariantCulture);
}
