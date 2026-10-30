using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Base.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace AniMeido.Plugin.Base.Services;

public sealed class ActionCenterService : IAnimePlaybackProgressSink
{
    private const string PlanColumns = """
        AnimeId, TitleSnapshot, Priority, TargetStartDate,
        SortOrder, CreatedAt, UpdatedAt, StartedAt, ArchivedAt
        """;
    private const string ReminderColumns = """
        ReminderId, AnimeId, Kind, RelativeDays, TimeOfDay,
        AbsoluteAt, ScheduledFor, State, CatchUpSentAt, HandledAt
        """;
    private readonly SqliteConnectionFactory _dbFactory;
    private readonly PlaybackProgressService _playbackProgress;
    internal PersonalDataAccess Access => _dbFactory.Access;

    public ActionCenterService(SqliteConnectionFactory dbFactory)
    {
        _dbFactory = dbFactory;
        _playbackProgress = new PlaybackProgressService(dbFactory);
    }

    public async Task UpsertPlanAsync(
        int animeId,
        string title,
        AnimePlanPriority priority,
        DateOnly? targetStartDate,
        int sortOrder,
        CancellationToken cancellationToken = default)
    {
        using var operation = _dbFactory.Access.BeginWrite();
        if (animeId == 0) throw new ArgumentOutOfRangeException(nameof(animeId));
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        await Task.Run(async () =>
        {
            using var connection =
                await _dbFactory.OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            await UpsertPlanInTransactionAsync(
                connection,
                transaction,
                animeId,
                title,
                priority,
                targetStartDate,
                sortOrder,
                preserveSortOrder: false,
                now: FormatTimestamp(DateTimeOffset.UtcNow),
                cancellationToken: cancellationToken);
            transaction.Commit();
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a plan using a caller-owned transaction. Gateway imports can
    /// preserve the existing sort order while normal upserts replace it.
    /// </summary>
    internal static async Task UpsertPlanInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int animeId,
        string title,
        AnimePlanPriority priority,
        DateOnly? targetStartDate,
        int sortOrder,
        bool preserveSortOrder,
        string now,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
                SortOrder = CASE
                    WHEN @preserveSortOrder = 1
                    THEN anime_plans.SortOrder
                    ELSE excluded.SortOrder
                END,
                UpdatedAt = excluded.UpdatedAt,
                ArchivedAt = NULL
            """;
        command.Parameters.AddWithValue("@animeId", animeId);
        command.Parameters.AddWithValue("@title", title.Trim());
        command.Parameters.AddWithValue("@priority", (int)priority);
        command.Parameters.AddWithValue(
            "@targetDate",
            targetStartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@sortOrder", sortOrder);
        command.Parameters.AddWithValue("@now", now);
        command.Parameters.AddWithValue(
            "@preserveSortOrder",
            preserveSortOrder ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<IReadOnlyList<AnimePlan>> GetPlansAsync(
        bool includeArchived = false,
        CancellationToken cancellationToken = default)
        // SQLite 的异步 API 仍可能同步等待锁；整次查询离开调用线程，不逐行调度。
        => Task.Run(async () =>
        {
            using var connection =
                await _dbFactory.OpenAsync(cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT {PlanColumns}
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
            return await ReadAllAsync(
                command,
                ReadPlan,
                cancellationToken);
        }, cancellationToken);

    public Task<AnimePlan?> GetPlanAsync(
        int animeId,
        CancellationToken cancellationToken = default)
        => Task.Run(() => ReadPlanAsync(animeId, cancellationToken), cancellationToken);

    private async Task<AnimePlan?> ReadPlanAsync(int animeId, CancellationToken cancellationToken)
    {
        using var connection =
            await _dbFactory.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {PlanColumns}
            FROM anime_plans
            WHERE AnimeId = @animeId
            """;
        command.Parameters.AddWithValue("@animeId", animeId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadPlan(reader)
            : null;
    }

    public async Task AddReminderAsync(
        PlanReminder reminder,
        CancellationToken cancellationToken = default)
    {
        using var operation = _dbFactory.Access.BeginWrite();
        ArgumentNullException.ThrowIfNull(reminder);
        await Task.Run(async () =>
        {
            using var connection =
                await _dbFactory.OpenAsync(cancellationToken);
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
        }).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<PlanReminder>> GetRemindersAsync(
        int? animeId = null,
        PlanReminderState? state = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => ReadRemindersAsync(animeId, state, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<PlanReminder>> ReadRemindersAsync(
        int? animeId, PlanReminderState? state, CancellationToken cancellationToken)
    {
        using var connection =
            await _dbFactory.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {ReminderColumns}
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
        var reminders = await ReadAllAsync(
            command,
            ReadReminder,
            cancellationToken);
        return reminders.OrderBy(reminder => reminder.ScheduledFor).ToArray();
    }

    public Task<IReadOnlyList<PlanReminder>> GetRecentUnprocessedRemindersAsync(
        DateTimeOffset now,
        TimeSpan catchUpWindow,
        CancellationToken cancellationToken = default)
        => Task.Run(
            () => ReadRecentUnprocessedRemindersAsync(now, catchUpWindow, cancellationToken),
            cancellationToken);

    private async Task<IReadOnlyList<PlanReminder>> ReadRecentUnprocessedRemindersAsync(
        DateTimeOffset now, TimeSpan catchUpWindow, CancellationToken cancellationToken)
    {
        using var connection =
            await _dbFactory.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {ReminderColumns}
            FROM plan_reminders
            WHERE State = @pending
              AND CatchUpSentAt IS NULL
            ORDER BY ScheduledFor
            """;
        command.Parameters.AddWithValue(
            "@pending",
            (int)PlanReminderState.Pending);
        var oldest = now.Subtract(catchUpWindow);
        var reminders = await ReadAllAsync(
            command,
            ReadReminder,
            cancellationToken);
        return reminders
            .Where(reminder => reminder.ScheduledFor >= oldest && reminder.ScheduledFor <= now)
            .OrderBy(reminder => reminder.ScheduledFor)
            .ToArray();
    }

    public Task MarkReminderCatchUpSentAsync(
        string reminderId,
        CancellationToken cancellationToken = default)
        => UpdateReminderAsync(
            reminderId,
            "CatchUpSentAt = @now",
            state: null,
            cancellationToken: cancellationToken);

    public async Task<bool> TryMarkReminderHandledAsync(
        string reminderId,
        CancellationToken cancellationToken = default)
    {
        using var operation = _dbFactory.Access.BeginWrite();
        return await Task.Run(async () =>
        {
            using var connection =
                await _dbFactory.OpenAsync(cancellationToken);
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
                FormatTimestamp(DateTimeOffset.UtcNow));
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }).ConfigureAwait(false);
    }

    public Task CancelReminderAsync(
        string reminderId,
        CancellationToken cancellationToken = default)
        => UpdateReminderAsync(
            reminderId,
            "State = @state",
            PlanReminderState.Cancelled,
            cancellationToken);

    public Task RecordAsync(
        AnimePlaybackProgress progress,
        CancellationToken cancellationToken = default)
        => _playbackProgress.RecordAsync(progress, cancellationToken);

    public Task<IReadOnlyDictionary<int, AnimeProgressSnapshot>> GetProgressAsync(
        CancellationToken cancellationToken = default)
        => _playbackProgress.GetProgressAsync(cancellationToken);

    private static async Task<IReadOnlyList<T>> ReadAllAsync<T>(
        SqliteCommand command,
        Func<SqliteDataReader, T> map,
        CancellationToken cancellationToken)
    {
        var items = new List<T>();
        using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(map(reader));
        }

        return items;
    }

    private async Task UpdateReminderAsync(
        string reminderId,
        string setClause,
        PlanReminderState? state,
        CancellationToken cancellationToken)
    {
        using var operation = _dbFactory.Access.BeginWrite();
        await Task.Run(async () =>
        {
            using var connection =
                await _dbFactory.OpenAsync(cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText =
                $"UPDATE plan_reminders SET {setClause} WHERE ReminderId = @id";
            command.Parameters.AddWithValue("@id", reminderId);
            command.Parameters.AddWithValue(
                "@now",
                FormatTimestamp(DateTimeOffset.UtcNow));
            if (state is not null)
            {
                command.Parameters.AddWithValue("@state", (int)state.Value);
            }
            await command.ExecuteNonQueryAsync(cancellationToken);
        }).ConfigureAwait(false);
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
            FormatOptionalTimestamp(reminder.AbsoluteAt));
        command.Parameters.AddWithValue(
            "@scheduledFor",
            FormatTimestamp(reminder.ScheduledFor));
        command.Parameters.AddWithValue("@state", (int)reminder.State);
        command.Parameters.AddWithValue(
            "@catchUp",
            FormatOptionalTimestamp(reminder.CatchUpSentAt));
        command.Parameters.AddWithValue(
            "@handled",
            FormatOptionalTimestamp(reminder.HandledAt));
    }

    private static DateTimeOffset ParseTimestamp(string value)
        => DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private static string FormatTimestamp(DateTimeOffset value)
        => value.ToString("O", CultureInfo.InvariantCulture);

    private static object FormatOptionalTimestamp(DateTimeOffset? value)
        => value is null
            ? DBNull.Value
            : FormatTimestamp(value.Value);
}
