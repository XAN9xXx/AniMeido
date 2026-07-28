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
            using var connection = await _dbFactory.OpenAsync();
            using var transaction = connection.BeginTransaction();
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
            var updatedAt = DateTime.UtcNow.ToString("O");
            command.Parameters.AddWithValue("@updatedAt", updatedAt);

            await command.ExecuteNonQueryAsync();
            await SynchronizePlanAsync(
                connection,
                transaction,
                animeId,
                status,
                updatedAt);
            transaction.Commit();
            if (status != AnimeTrackingStatus.PlanToWatch)
            {
                await CancelPlanNotificationsAsync(animeId);
            }
        }

        /// <summary>
        /// 导入专用方法：写入指定状态和原始 UpdatedAt，保留导出时的时间戳。
        /// </summary>
        public async Task SetStatusWithTimestampAsync(int animeId, AnimeTrackingStatus status, string updatedAt)
        {
            using var connection = await _dbFactory.OpenAsync();
            using var transaction = connection.BeginTransaction();
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

            await command.ExecuteNonQueryAsync();
            await SynchronizePlanAsync(
                connection,
                transaction,
                animeId,
                status,
                updatedAt);
            transaction.Commit();
            if (status != AnimeTrackingStatus.PlanToWatch)
            {
                await CancelPlanNotificationsAsync(animeId);
            }
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
                DateTime.UtcNow.ToString("O"));
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

        private static async Task SynchronizePlanAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int animeId,
            AnimeTrackingStatus status,
            string updatedAt)
        {
            if (status == AnimeTrackingStatus.PlanToWatch)
            {
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
                await plan.ExecuteNonQueryAsync();
                return;
            }

            await ArchivePlanAsync(
                connection,
                transaction,
                animeId,
                updatedAt);
        }

        private static async Task ArchivePlanAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int animeId,
            string updatedAt)
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
                await plan.ExecuteNonQueryAsync();
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
            await reminders.ExecuteNonQueryAsync();
        }

        private Task CancelPlanNotificationsAsync(int animeId)
            => _notifications?.CancelGroupAsync(
                PlanReminderCoordinator.GetNotificationGroup(animeId))
                ?? Task.CompletedTask;

    }
}

