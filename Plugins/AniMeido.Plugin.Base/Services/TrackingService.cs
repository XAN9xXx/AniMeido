using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Contracts.Notifications;
using AniMeido.Plugin.Base.Models;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace AniMeido.Plugin.Base.Services
{
    public class TrackingService
    {
        private readonly SqliteConnectionFactory _dbFactory;
        private readonly IAppNotificationService? _notifications;
        private static readonly JsonSerializerOptions ConfigJsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public TrackingService(
            SqliteConnectionFactory dbFactory,
            IAppNotificationService? notifications = null)
        {
            _dbFactory = dbFactory;
            _notifications = notifications;
        }

        public async Task SetStatusAsync(int animeId, AnimeTrackingStatus status)
        {
            var updatedAt = DateTime.UtcNow.ToString("O");
            await SetStatusCoreAsync(
                animeId,
                status,
                updatedAt,
                recordEvent: true);
        }

        /// <summary>
        /// 导入专用方法：写入指定状态和原始 UpdatedAt，保留导出时的时间戳。
        /// </summary>
        public async Task SetStatusWithTimestampAsync(int animeId, AnimeTrackingStatus status, string updatedAt)
            => await SetStatusCoreAsync(
                animeId,
                status,
                updatedAt,
                recordEvent: false);

        private async Task SetStatusCoreAsync(
            int animeId,
            AnimeTrackingStatus status,
            string updatedAt,
            bool recordEvent)
        {
            using var connection = await _dbFactory.OpenAsync();
            using var transaction = connection.BeginTransaction();
            await SetStatusInTransactionAsync(
                connection,
                transaction,
                animeId,
                status,
                updatedAt,
                recordEvent,
                recordOnlyOnChange: true,
                eventId: null,
                cancellationToken: CancellationToken.None);
            transaction.Commit();
            if (status != AnimeTrackingStatus.PlanToWatch)
            {
                await CancelPlanNotificationsAsync(animeId);
            }
        }

        /// <summary>
        /// Writes tracking state and plan synchronization using a caller-owned
        /// transaction. The caller controls event recording semantics.
        /// </summary>
        internal static async Task SetStatusInTransactionAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int animeId,
            AnimeTrackingStatus status,
            string updatedAt,
            bool recordEvent,
            bool recordOnlyOnChange,
            string? eventId,
            CancellationToken cancellationToken)
        {
            AnimeTrackingStatus? previousStatus = null;
            using (var previous = connection.CreateCommand())
            {
                previous.Transaction = transaction;
                previous.CommandText =
                    "SELECT Status FROM tracking WHERE AnimeId = @animeId";
                previous.Parameters.AddWithValue("@animeId", animeId);
                var value = await previous.ExecuteScalarAsync(cancellationToken);
                if (value is not null and not DBNull)
                {
                    previousStatus = (AnimeTrackingStatus)Convert.ToInt32(
                        value);
                }
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO tracking (AnimeId, Status, UpdatedAt)
                VALUES (@animeId, @status, @updatedAt)
                ON CONFLICT(AnimeId) DO UPDATE SET
                    Status = excluded.Status,
                    UpdatedAt = excluded.UpdatedAt
                """;
            command.Parameters.AddWithValue("@animeId", animeId);
            command.Parameters.AddWithValue("@status", (int)status);
            command.Parameters.AddWithValue("@updatedAt", updatedAt);

            await command.ExecuteNonQueryAsync(cancellationToken);
            if (recordEvent
                && (!recordOnlyOnChange || previousStatus != status))
            {
                using var eventCommand = connection.CreateCommand();
                eventCommand.Transaction = transaction;
                eventCommand.CommandText = """
                    INSERT INTO tracking_events(
                        EventId, AnimeId, PreviousStatus, NewStatus, ChangedAt)
                    VALUES(@id, @animeId, @previous, @next, @changedAt)
                    """;
                eventCommand.Parameters.AddWithValue(
                    "@id",
                    eventId ?? Guid.NewGuid().ToString("N"));
                eventCommand.Parameters.AddWithValue("@animeId", animeId);
                eventCommand.Parameters.AddWithValue(
                    "@previous",
                    previousStatus is null
                        ? DBNull.Value
                        : (int)previousStatus.Value);
                eventCommand.Parameters.AddWithValue("@next", (int)status);
                eventCommand.Parameters.AddWithValue("@changedAt", updatedAt);
                await eventCommand.ExecuteNonQueryAsync(cancellationToken);
            }
            await SynchronizePlanAsync(
                connection,
                transaction,
                animeId,
                previousStatus,
                status,
                updatedAt,
                cancellationToken);
        }

        /// <summary>Toggle only the Following mark without replacing another tracking status.</summary>
        internal async Task<(AnimeTrackingStatus? Status, bool Changed)> ToggleFollowingAsync(
            int animeId, CancellationToken cancellationToken)
        {
            using var connection = await _dbFactory.OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT Status FROM tracking WHERE AnimeId = @animeId";
            command.Parameters.AddWithValue("@animeId", animeId);
            var existing = await command.ExecuteScalarAsync(cancellationToken);
            if (existing is not null and not DBNull)
            {
                var status = (AnimeTrackingStatus)Convert.ToInt32(existing);
                if (status != AnimeTrackingStatus.Following)
                    return (status, false);

                command.CommandText = "DELETE FROM tracking WHERE AnimeId = @animeId AND Status = @following";
                command.Parameters.AddWithValue("@following", (int)AnimeTrackingStatus.Following);
                await command.ExecuteNonQueryAsync(cancellationToken);
                transaction.Commit();
                return (null, true);
            }

            await SetStatusInTransactionAsync(connection, transaction, animeId,
                AnimeTrackingStatus.Following, DateTime.UtcNow.ToString("O"),
                recordEvent: true, recordOnlyOnChange: true, eventId: null, cancellationToken);
            transaction.Commit();
            return (AnimeTrackingStatus.Following, true);
        }

        public async Task<AnimeTrackingStatus?> GetStatusAsync(int animeId)
        {
            using var connection = await _dbFactory.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT Status FROM tracking WHERE AnimeId = @animeId";
            command.Parameters.AddWithValue("@animeId", animeId);

            var result = await command.ExecuteScalarAsync();
            if (result is null) return null;

            return (AnimeTrackingStatus)Convert.ToInt32(result);
        }

        public async Task<List<int>> GetAnimeIdsByStatusAsync(AnimeTrackingStatus status)
        {
            using var connection = await _dbFactory.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT AnimeId FROM tracking WHERE Status = @status";
            command.Parameters.AddWithValue("@status", (int)status);

            var list = new List<int>();
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Convert.ToInt32(reader.GetInt64(0)));
            }
            return list;
        }

        public async Task<Dictionary<AnimeTrackingStatus, List<int>>>
            GetAnimeIdsGroupedByStatusAsync()
        {
            using var connection = await _dbFactory.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT AnimeId, Status
                FROM tracking
                ORDER BY UpdatedAt DESC
                """;

            var result =
                new Dictionary<AnimeTrackingStatus, List<int>>();
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var animeId = Convert.ToInt32(reader.GetInt64(0));
                var status = (AnimeTrackingStatus)Convert.ToInt32(
                    reader.GetInt64(1));
                if (!result.TryGetValue(status, out var ids))
                {
                    ids = [];
                    result.Add(status, ids);
                }

                ids.Add(animeId);
            }

            return result;
        }

        public async Task<HashSet<int>> GetBlockedAnimeIdsAsync()
        {
            var blocked = await GetAnimeIdsByStatusAsync(
                AnimeTrackingStatus.Blocked);
            return blocked.ToHashSet();
        }

        public async Task<bool> RemoveStatusAsync(int animeId)
        {
            using var connection = await _dbFactory.OpenAsync();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM tracking WHERE AnimeId = @animeId";
            command.Parameters.AddWithValue("@animeId", animeId);

            var removed = await command.ExecuteNonQueryAsync() == 1;
            await ArchivePlanAsync(
                connection,
                transaction,
                animeId,
                DateTime.UtcNow.ToString("O"),
                CancellationToken.None);
            transaction.Commit();
            await CancelPlanNotificationsAsync(animeId);
            return removed;
        }

        /// <summary>
        /// 获取所有追番记录（用于导出）。
        /// </summary>
        public async Task<List<(int AnimeId, AnimeTrackingStatus Status, string UpdatedAt)>> GetAllTrackingAsync()
        {
            using var connection = await _dbFactory.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT AnimeId, Status, UpdatedAt FROM tracking ORDER BY UpdatedAt DESC";

            var list = new List<(int, AnimeTrackingStatus, string)>();
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var animeId = Convert.ToInt32(reader.GetInt64(0));
                var status = (AnimeTrackingStatus)Convert.ToInt32(reader.GetInt64(1));
                var updatedAt = reader.IsDBNull(2) ? "" : reader.GetString(2);
                list.Add((animeId, status, updatedAt));
            }
            return list;
        }

        // ======== 拖放配置 ========

        public async Task SaveDragZoneConfigAsync(List<DragZoneConfig> configs)
        {
            var json = JsonSerializer.Serialize(configs, ConfigJsonOptions);

            using var connection = await _dbFactory.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR REPLACE INTO config (Key, Value)
                VALUES ('drag_zones', @value)
                """;
            command.Parameters.AddWithValue("@value", json);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<List<DragZoneConfig>> LoadDragZoneConfigAsync()
        {
            using var connection = await _dbFactory.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT Value FROM config WHERE Key = 'drag_zones'";
            var result = await command.ExecuteScalarAsync();

            if (result is string json && !string.IsNullOrEmpty(json))
            {
                var zones = JsonSerializer.Deserialize<List<DragZoneConfig>>(json, ConfigJsonOptions)
                    ?? DragZoneConfig.GetDefaults();
                return zones;
            }

            return DragZoneConfig.GetDefaults();
        }

        // ======== 放送日历“今日一抽” ========

        private const string CalendarDailyPickKey = "calendar_daily_pick";

        private sealed record CalendarDailyPickState(string Date, int AnimeId);

        /// <summary>读取某天抽到的作品；没有记录或内容无法解析时返回 null。</summary>
        public async Task<(DateOnly Date, int AnimeId)?> LoadCalendarDailyPickAsync()
        {
            using var connection = await _dbFactory.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT Value FROM config WHERE Key = @key";
            command.Parameters.AddWithValue("@key", CalendarDailyPickKey);
            if (await command.ExecuteScalarAsync() is not string json
                || string.IsNullOrEmpty(json))
            {
                return null;
            }

            try
            {
                var state = JsonSerializer.Deserialize<CalendarDailyPickState>(json, ConfigJsonOptions);
                return state is not null
                    && DateOnly.TryParseExact(
                        state.Date,
                        "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out var date)
                    ? (date, state.AnimeId)
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>记下某天抽到的作品，重启后同一天仍显示它。</summary>
        public async Task SaveCalendarDailyPickAsync(DateOnly date, int animeId)
        {
            var json = JsonSerializer.Serialize(
                new CalendarDailyPickState(
                    date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    animeId),
                ConfigJsonOptions);

            using var connection = await _dbFactory.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR REPLACE INTO config (Key, Value)
                VALUES (@key, @value)
                """;
            command.Parameters.AddWithValue("@key", CalendarDailyPickKey);
            command.Parameters.AddWithValue("@value", json);
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// 读取全部状态变化记录（今日主题“一年前的今天”使用），不含移除标记。
        /// 时间可能来自导入，格式不一，这里逐条解析，解析不了的跳过。
        /// </summary>
        public async Task<IReadOnlyList<(int AnimeId, AnimeTrackingStatus NewStatus, DateTimeOffset ChangedAt)>>
            GetTrackingEventsAsync(CancellationToken cancellationToken = default)
        {
            using var connection = await _dbFactory.OpenAsync(cancellationToken);

            var command = connection.CreateCommand();
            command.CommandText = "SELECT AnimeId, NewStatus, ChangedAt FROM tracking_events";
            var events = new List<(int, AnimeTrackingStatus, DateTimeOffset)>();
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.IsDBNull(2)
                    || !DateTimeOffset.TryParse(
                        reader.GetString(2),
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal,
                        out var changedAt))
                {
                    continue;
                }

                events.Add((reader.GetInt32(0), (AnimeTrackingStatus)reader.GetInt32(1), changedAt));
            }

            return events;
        }

        private const string TimeMachineShownKey = "time_machine_shown";

        private sealed record TimeMachineShownJson(int Year, string Season, IReadOnlyList<int> AnimeIds);

        /// <summary>
        /// 读取番剧时光机每一季显示过的作品（跨重启），用于下次优先抽没显示过的；
        /// 没有记录或无法解析时返回空。
        /// </summary>
        public async Task<IReadOnlyDictionary<PastSeasonTarget, IReadOnlyList<int>>> LoadTimeMachineShownAsync()
        {
            using var connection = await _dbFactory.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT Value FROM config WHERE Key = @key";
            command.Parameters.AddWithValue("@key", TimeMachineShownKey);
            if (await command.ExecuteScalarAsync() is not string json
                || string.IsNullOrEmpty(json))
            {
                return new Dictionary<PastSeasonTarget, IReadOnlyList<int>>();
            }

            try
            {
                return (JsonSerializer.Deserialize<List<TimeMachineShownJson>>(json, ConfigJsonOptions) ?? [])
                    .Where(item => Enum.TryParse<Season>(item.Season, out var season) && Enum.IsDefined(season))
                    .GroupBy(item => new PastSeasonTarget(item.Year, Enum.Parse<Season>(item.Season)))
                    .ToDictionary(
                        group => group.Key,
                        group => (IReadOnlyList<int>)group.SelectMany(item => item.AnimeIds ?? []).Distinct().ToList());
            }
            catch (JsonException)
            {
                return new Dictionary<PastSeasonTarget, IReadOnlyList<int>>();
            }
        }

        /// <summary>记下番剧时光机每一季显示过的作品。</summary>
        public async Task SaveTimeMachineShownAsync(IReadOnlyDictionary<PastSeasonTarget, IReadOnlyList<int>> shown)
        {
            var json = JsonSerializer.Serialize(
                shown
                    .OrderBy(pair => pair.Key.Year)
                    .ThenBy(pair => pair.Key.Season)
                    .Select(pair => new TimeMachineShownJson(pair.Key.Year, pair.Key.Season.ToString(), pair.Value))
                    .ToList(),
                ConfigJsonOptions);

            using var connection = await _dbFactory.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR REPLACE INTO config (Key, Value)
                VALUES (@key, @value)
                """;
            command.Parameters.AddWithValue("@key", TimeMachineShownKey);
            command.Parameters.AddWithValue("@value", json);
            await command.ExecuteNonQueryAsync();
        }

        private const string TodayThemeKey = "today_theme";

        private sealed record TodayThemeStateJson(
            string Date,
            string Theme,
            bool Fallback,
            IReadOnlyList<int> AnimeIds);

        /// <summary>读取今天页“今日主题”记下的主题与这一批作品；没有记录或无法解析时返回 null。</summary>
        public async Task<TodayThemeState?> LoadTodayThemeAsync()
        {
            using var connection = await _dbFactory.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT Value FROM config WHERE Key = @key";
            command.Parameters.AddWithValue("@key", TodayThemeKey);
            if (await command.ExecuteScalarAsync() is not string json
                || string.IsNullOrEmpty(json))
            {
                return null;
            }

            try
            {
                var state = JsonSerializer.Deserialize<TodayThemeStateJson>(json, ConfigJsonOptions);
                return state is not null
                    && DateOnly.TryParseExact(
                        state.Date,
                        "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out var date)
                    && Enum.TryParse<TodayThemeKind>(state.Theme, out var theme)
                    && Enum.IsDefined(theme)
                    ? new TodayThemeState(date, theme, state.Fallback, state.AnimeIds ?? [])
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>记下今天显示的主题与这一批作品，重启后同一天按它恢复。</summary>
        public async Task SaveTodayThemeAsync(TodayThemeState state)
        {
            var json = JsonSerializer.Serialize(
                new TodayThemeStateJson(
                    state.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    state.Theme.ToString(),
                    state.IsFallback,
                    state.AnimeIds),
                ConfigJsonOptions);

            using var connection = await _dbFactory.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR REPLACE INTO config (Key, Value)
                VALUES (@key, @value)
                """;
            command.Parameters.AddWithValue("@key", TodayThemeKey);
            command.Parameters.AddWithValue("@value", json);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task SynchronizePlanAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int animeId,
            AnimeTrackingStatus? previousStatus,
            AnimeTrackingStatus status,
            string updatedAt,
            CancellationToken cancellationToken)
        {
            if (status == AnimeTrackingStatus.PlanToWatch)
            {
                // 只有真正切换到补番中时才重新激活计划；重复写入同一状态
                // 不得恢复已归档的计划（包括旧版本中标记过开始的计划）。
                if (previousStatus == AnimeTrackingStatus.PlanToWatch)
                {
                    return;
                }

                using var plan = connection.CreateCommand();
                plan.Transaction = transaction;
                plan.CommandText = """
                    UPDATE anime_plans
                    SET ArchivedAt = NULL,
                        StartedAt = NULL,
                        UpdatedAt = @updatedAt
                    WHERE AnimeId = @animeId
                    """;
                plan.Parameters.AddWithValue("@animeId", animeId);
                plan.Parameters.AddWithValue("@updatedAt", updatedAt);
                await plan.ExecuteNonQueryAsync(cancellationToken);
                return;
            }

            await ArchivePlanAsync(
                connection,
                transaction,
                animeId,
                updatedAt,
                cancellationToken);
        }

        private static async Task ArchivePlanAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int animeId,
            string updatedAt,
            CancellationToken cancellationToken)
        {
            using (var plan = connection.CreateCommand())
            {
                plan.Transaction = transaction;
                plan.CommandText = """
                    UPDATE anime_plans
                    SET ArchivedAt = COALESCE(ArchivedAt, @updatedAt),
                        UpdatedAt = @updatedAt
                    WHERE AnimeId = @animeId
                    """;
                plan.Parameters.AddWithValue("@animeId", animeId);
                plan.Parameters.AddWithValue("@updatedAt", updatedAt);
                await plan.ExecuteNonQueryAsync(cancellationToken);
            }

            using var reminders = connection.CreateCommand();
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

        private Task CancelPlanNotificationsAsync(int animeId)
            => _notifications?.CancelGroupAsync(
                PlanReminderCoordinator.GetNotificationGroup(animeId))
                ?? Task.CompletedTask;

    }
}

