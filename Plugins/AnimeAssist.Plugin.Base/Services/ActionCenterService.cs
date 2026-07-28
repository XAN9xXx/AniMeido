using AniMeido.Contracts.Models;
using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Base.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace AniMeido.Plugin.Base.Services;

public sealed class ActionCenterService : IAnimePlaybackProgressSink
{
    private static readonly JsonSerializerOptions SmartListJsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly SqliteConnectionFactory _dbFactory;

    public ActionCenterService(SqliteConnectionFactory dbFactory)
        => _dbFactory = dbFactory;

    public async Task UpsertPlanAsync(
        int animeId,
        string title,
        AnimePlanPriority priority,
        DateOnly? targetStartDate,
        int sortOrder,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(animeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        using var connection = await _dbFactory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO anime_plans(
                AnimeId, TitleSnapshot, Priority, TargetStartDate,
                SortOrder, CreatedAt, UpdatedAt, StartedAt, ArchivedAt)
            VALUES(
                @animeId, @title, @priority, @targetDate,
                @sortOrder, @now, @now, NULL, NULL)
            ON CONFLICT(AnimeId) DO UPDATE SET
                TitleSnapshot = excluded.TitleSnapshot,
                Priority = excluded.Priority,
                TargetStartDate = excluded.TargetStartDate,
                SortOrder = excluded.SortOrder,
                UpdatedAt = excluded.UpdatedAt,
                ArchivedAt = NULL
            """;
        var now = DateTimeOffset.UtcNow.ToString("O");
        command.Parameters.AddWithValue("@animeId", animeId);
        command.Parameters.AddWithValue("@title", title.Trim());
        command.Parameters.AddWithValue("@priority", (int)priority);
        command.Parameters.AddWithValue(
            "@targetDate",
            targetStartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@sortOrder", sortOrder);
        command.Parameters.AddWithValue("@now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AnimePlan>> GetPlansAsync(
        bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        using var connection = await _dbFactory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT AnimeId, TitleSnapshot, Priority, TargetStartDate,
                   SortOrder, CreatedAt, UpdatedAt, StartedAt, ArchivedAt
            FROM anime_plans
            WHERE @includeArchived = 1 OR ArchivedAt IS NULL
            ORDER BY Priority DESC,
                     CASE WHEN TargetStartDate IS NULL THEN 1 ELSE 0 END,
                     TargetStartDate,
                     SortOrder,
                     UpdatedAt DESC
            """;
        command.Parameters.AddWithValue(
            "@includeArchived",
            includeArchived ? 1 : 0);
        var plans = new List<AnimePlan>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            plans.Add(ReadPlan(reader));
        }

        return plans;
    }

    public async Task<AnimePlan?> GetPlanAsync(
        int animeId,
        CancellationToken cancellationToken = default)
    {
        using var connection = await _dbFactory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT AnimeId, TitleSnapshot, Priority, TargetStartDate,
                   SortOrder, CreatedAt, UpdatedAt, StartedAt, ArchivedAt
            FROM anime_plans
            WHERE AnimeId = @animeId
            """;
        command.Parameters.AddWithValue("@animeId", animeId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadPlan(reader)
            : null;
    }

    public async Task StartPlanAsync(
        int animeId,
        CancellationToken cancellationToken = default)
    {
        using var connection = await _dbFactory.OpenAsync();
        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("O");
        using (var tracking = connection.CreateCommand())
        {
            tracking.Transaction = transaction;
            tracking.CommandText = """
                INSERT INTO tracking(AnimeId, Status, UpdatedAt)
                VALUES(@animeId, @status, @now)
                ON CONFLICT(AnimeId) DO UPDATE SET
                    Status = excluded.Status,
                    UpdatedAt = excluded.UpdatedAt
                """;
            tracking.Parameters.AddWithValue("@animeId", animeId);
            tracking.Parameters.AddWithValue(
                "@status",
                (int)AnimeTrackingStatus.Watching);
            tracking.Parameters.AddWithValue("@now", now);
            await tracking.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var plan = connection.CreateCommand())
        {
            plan.Transaction = transaction;
            plan.CommandText = """
                UPDATE anime_plans
                SET StartedAt = COALESCE(StartedAt, @now),
                    ArchivedAt = @now,
                    UpdatedAt = @now
                WHERE AnimeId = @animeId
                """;
            plan.Parameters.AddWithValue("@animeId", animeId);
            plan.Parameters.AddWithValue("@now", now);
            await plan.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var reminders = connection.CreateCommand())
        {
            reminders.Transaction = transaction;
            reminders.CommandText = """
                UPDATE plan_reminders
                SET State = @cancelled
                WHERE AnimeId = @animeId AND State = @pending
                """;
            reminders.Parameters.AddWithValue("@animeId", animeId);
            reminders.Parameters.AddWithValue(
                "@cancelled",
                (int)PlanReminderState.Cancelled);
            reminders.Parameters.AddWithValue(
                "@pending",
                (int)PlanReminderState.Pending);
            await reminders.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
    }

    public async Task AddReminderAsync(
        PlanReminder reminder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reminder);
        using var connection = await _dbFactory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO plan_reminders(
                ReminderId, AnimeId, Kind, RelativeDays, TimeOfDay,
                AbsoluteAt, ScheduledFor, State, CatchUpSentAt, HandledAt)
            VALUES(
                @id, @animeId, @kind, @relativeDays, @timeOfDay,
                @absoluteAt, @scheduledFor, @state, @catchUp, @handled)
            ON CONFLICT(ReminderId) DO UPDATE SET
                AnimeId = excluded.AnimeId,
                Kind = excluded.Kind,
                RelativeDays = excluded.RelativeDays,
                TimeOfDay = excluded.TimeOfDay,
                AbsoluteAt = excluded.AbsoluteAt,
                ScheduledFor = excluded.ScheduledFor,
                State = excluded.State,
                CatchUpSentAt = excluded.CatchUpSentAt,
                HandledAt = excluded.HandledAt
            """;
        AddReminderParameters(command, reminder);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PlanReminder>> GetRemindersAsync(
        int? animeId = null,
        PlanReminderState? state = null,
        CancellationToken cancellationToken = default)
    {
        using var connection = await _dbFactory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ReminderId, AnimeId, Kind, RelativeDays, TimeOfDay,
                   AbsoluteAt, ScheduledFor, State, CatchUpSentAt, HandledAt
            FROM plan_reminders
            WHERE (@animeId IS NULL OR AnimeId = @animeId)
              AND (@state IS NULL OR State = @state)
            ORDER BY ScheduledFor
            """;
        command.Parameters.AddWithValue(
            "@animeId",
            animeId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "@state",
            state is null ? DBNull.Value : (int)state.Value);
        var reminders = new List<PlanReminder>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            reminders.Add(ReadReminder(reader));
        }

        return reminders;
    }

    public async Task<IReadOnlyList<PlanReminder>>
        GetRecentUnprocessedRemindersAsync(
            DateTimeOffset now,
            TimeSpan catchUpWindow,
            CancellationToken cancellationToken = default)
    {
        using var connection = await _dbFactory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ReminderId, AnimeId, Kind, RelativeDays, TimeOfDay,
                   AbsoluteAt, ScheduledFor, State, CatchUpSentAt, HandledAt
            FROM plan_reminders
            WHERE State = @pending
              AND CatchUpSentAt IS NULL
              AND ScheduledFor <= @now
              AND ScheduledFor >= @oldest
            ORDER BY ScheduledFor
            """;
        command.Parameters.AddWithValue(
            "@pending",
            (int)PlanReminderState.Pending);
        command.Parameters.AddWithValue("@now", now.ToString("O"));
        command.Parameters.AddWithValue(
            "@oldest",
            now.Subtract(catchUpWindow).ToString("O"));
        var reminders = new List<PlanReminder>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            reminders.Add(ReadReminder(reader));
        }

        return reminders;
    }

    public Task MarkReminderCatchUpSentAsync(
        string reminderId,
        CancellationToken cancellationToken = default)
        => UpdateReminderAsync(
            reminderId,
            "CatchUpSentAt = @now",
            cancellationToken);

    public async Task<bool> TryMarkReminderHandledAsync(
        string reminderId,
        CancellationToken cancellationToken = default)
    {
        using var connection = await _dbFactory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE plan_reminders
            SET State = @handled, HandledAt = @now
            WHERE ReminderId = @id AND State = @pending
            """;
        command.Parameters.AddWithValue("@id", reminderId);
        command.Parameters.AddWithValue(
            "@handled",
            (int)PlanReminderState.Handled);
        command.Parameters.AddWithValue(
            "@pending",
            (int)PlanReminderState.Pending);
        command.Parameters.AddWithValue(
            "@now",
            DateTimeOffset.UtcNow.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public Task CancelReminderAsync(
        string reminderId,
        CancellationToken cancellationToken = default)
        => UpdateReminderAsync(
            reminderId,
            "State = 2",
            cancellationToken);

    public async Task RecordAsync(
        AnimePlaybackProgress progress,
        CancellationToken cancellationToken = default)
    {
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
        using var connection = await _dbFactory.OpenAsync();
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
                        WHEN tracking.Status IN (@plan, @following)
                        THEN @watching
                        ELSE tracking.Status END,
                    UpdatedAt = CASE
                        WHEN tracking.Status IN (@plan, @following)
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
                "@plan",
                (int)AnimeTrackingStatus.PlanToWatch);
            tracking.Parameters.AddWithValue(
                "@following",
                (int)AnimeTrackingStatus.Following);
            tracking.Parameters.AddWithValue(
                "@observedAt",
                progress.ObservedAt.UtcDateTime.ToString("O"));
            await tracking.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
    }

    public async Task<IReadOnlyDictionary<int, AnimeProgressSnapshot>>
        GetProgressAsync(
            CancellationToken cancellationToken = default)
    {
        using var connection = await _dbFactory.OpenAsync();
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

    public async Task SaveSmartListAsync(
        SmartListDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _ = SmartListEvaluator.Apply(definition, []);
        using var connection = await _dbFactory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO smart_lists(
                Id, Name, SchemaVersion, RuleJson, SortJson,
                CreatedAt, UpdatedAt)
            VALUES(
                @id, @name, @version, @rules, @sort,
                @createdAt, @updatedAt)
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name,
                SchemaVersion = excluded.SchemaVersion,
                RuleJson = excluded.RuleJson,
                SortJson = excluded.SortJson,
                UpdatedAt = excluded.UpdatedAt
            """;
        command.Parameters.AddWithValue("@id", definition.Id);
        command.Parameters.AddWithValue("@name", definition.Name);
        command.Parameters.AddWithValue(
            "@version",
            definition.SchemaVersion);
        command.Parameters.AddWithValue(
            "@rules",
            JsonSerializer.Serialize(
                definition.Rules,
                SmartListJsonOptions));
        command.Parameters.AddWithValue(
            "@sort",
            definition.Sort is null
                ? DBNull.Value
                : JsonSerializer.Serialize(
                    definition.Sort,
                    SmartListJsonOptions));
        command.Parameters.AddWithValue(
            "@createdAt",
            definition.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue(
            "@updatedAt",
            definition.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SmartListDefinition>> GetSmartListsAsync(
        CancellationToken cancellationToken = default)
    {
        using var connection = await _dbFactory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, SchemaVersion, RuleJson, SortJson,
                   CreatedAt, UpdatedAt
            FROM smart_lists
            ORDER BY Name COLLATE NOCASE
            """;
        var definitions = new List<SmartListDefinition>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var rules = JsonSerializer.Deserialize<SmartListRuleGroup>(
                reader.GetString(3),
                SmartListJsonOptions);
            if (rules is null)
            {
                continue;
            }

            var sort = reader.IsDBNull(4)
                ? null
                : JsonSerializer.Deserialize<SmartListSort>(
                    reader.GetString(4),
                    SmartListJsonOptions);
            definitions.Add(new SmartListDefinition(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                rules,
                sort,
                ParseTimestamp(reader.GetString(5)),
                ParseTimestamp(reader.GetString(6))));
        }

        return definitions;
    }

    public async Task DeleteSmartListAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        using var connection = await _dbFactory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM smart_lists WHERE Id = @id";
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdateReminderAsync(
        string reminderId,
        string setClause,
        CancellationToken cancellationToken)
    {
        using var connection = await _dbFactory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"UPDATE plan_reminders SET {setClause} WHERE ReminderId = @id";
        command.Parameters.AddWithValue("@id", reminderId);
        command.Parameters.AddWithValue(
            "@now",
            DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static AnimePlan ReadPlan(SqliteDataReader reader)
        => new(
            reader.GetInt32(0),
            reader.GetString(1),
            (AnimePlanPriority)reader.GetInt32(2),
            reader.IsDBNull(3)
                ? null
                : DateOnly.Parse(
                    reader.GetString(3),
                    CultureInfo.InvariantCulture),
            reader.GetInt32(4),
            ParseTimestamp(reader.GetString(5)),
            ParseTimestamp(reader.GetString(6)),
            reader.IsDBNull(7)
                ? null
                : ParseTimestamp(reader.GetString(7)),
            reader.IsDBNull(8)
                ? null
                : ParseTimestamp(reader.GetString(8)));

    private static PlanReminder ReadReminder(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetInt32(1),
            (PlanReminderKind)reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.IsDBNull(4)
                ? null
                : TimeOnly.Parse(
                    reader.GetString(4),
                    CultureInfo.InvariantCulture),
            reader.IsDBNull(5)
                ? null
                : ParseTimestamp(reader.GetString(5)),
            ParseTimestamp(reader.GetString(6)),
            (PlanReminderState)reader.GetInt32(7),
            reader.IsDBNull(8)
                ? null
                : ParseTimestamp(reader.GetString(8)),
            reader.IsDBNull(9)
                ? null
                : ParseTimestamp(reader.GetString(9)));

    private static void AddReminderParameters(
        SqliteCommand command,
        PlanReminder reminder)
    {
        command.Parameters.AddWithValue("@id", reminder.ReminderId);
        command.Parameters.AddWithValue("@animeId", reminder.AnimeId);
        command.Parameters.AddWithValue("@kind", (int)reminder.Kind);
        command.Parameters.AddWithValue(
            "@relativeDays",
            reminder.RelativeDays ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "@timeOfDay",
            reminder.TimeOfDay?.ToString(
                "HH:mm:ss",
                CultureInfo.InvariantCulture)
                ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "@absoluteAt",
            reminder.AbsoluteAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "@scheduledFor",
            reminder.ScheduledFor.ToString("O"));
        command.Parameters.AddWithValue("@state", (int)reminder.State);
        command.Parameters.AddWithValue(
            "@catchUp",
            reminder.CatchUpSentAt?.ToString("O")
                ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "@handled",
            reminder.HandledAt?.ToString("O")
                ?? (object)DBNull.Value);
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
            progress.ObservedAt.UtcDateTime.ToString("O"));
    }

    private static DateTimeOffset ParseTimestamp(string value)
        => DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
}
