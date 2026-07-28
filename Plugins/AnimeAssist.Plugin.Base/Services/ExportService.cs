using AniMeido.Contracts.Models;
using System.Text.Json;
using AniMeido.Plugin.Base.Models;
using System.Globalization;

namespace AniMeido.Plugin.Base.Services
{
    /// <summary>
    /// 个人数据 JSON 导出/导入服务。
    /// 导出内容：追番状态、拖放配置、标签及行动中心数据。
    /// 不包含：缓存数据（可重新拉取）。
    /// </summary>
    public class ExportService
    {
        private readonly TrackingService _tracking;
        private readonly SavedTagService _savedTagService;
        private readonly SqliteConnectionFactory _dbFactory;
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        private static readonly JsonSerializerOptions ConfigJsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public const int SchemaVersion = 3;
        private static readonly IReadOnlyDictionary<string, string[]> P4TableColumns =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["anime_plans"] =
                [
                    "AnimeId", "TitleSnapshot", "Priority", "TargetStartDate",
                    "SortOrder", "CreatedAt", "UpdatedAt", "StartedAt",
                    "ArchivedAt",
                ],
                ["plan_reminders"] =
                [
                    "ReminderId", "AnimeId", "Kind", "RelativeDays",
                    "TimeOfDay", "AbsoluteAt", "ScheduledFor", "State",
                    "CatchUpSentAt", "HandledAt",
                ],
                ["anime_progress"] =
                [
                    "AnimeId", "CurrentEpisode", "PositionSeconds",
                    "DurationSeconds", "LastWatchedAt",
                ],
                ["episode_progress"] =
                [
                    "AnimeId", "EpisodeNumber", "PositionSeconds",
                    "DurationSeconds", "IsCompleted", "LastWatchedAt",
                ],
                ["watch_sessions"] =
                [
                    "EventId", "AnimeId", "EpisodeNumber", "PositionSeconds",
                    "DurationSeconds", "IsCompleted", "ObservedAt",
                ],
                ["smart_lists"] =
                [
                    "Id", "Name", "SchemaVersion", "RuleJson", "SortJson",
                    "CreatedAt", "UpdatedAt",
                ],
            };



        public ExportService(TrackingService tracking, SavedTagService savedTagService, SqliteConnectionFactory dbFactory)
        {
            _tracking = tracking;
            _savedTagService = savedTagService;
            _dbFactory = dbFactory;
        }


        /// <summary>
        /// 导出追番数据为 JSON 字符串。
        /// </summary>
        public async Task<string> ExportAsync()
        {
            var appVersion = System.Reflection.Assembly.GetEntryAssembly()?.GetName()?.Version;
            var versionStr = appVersion != null
                ? $"v{appVersion.Major}.{appVersion.Minor}.{appVersion.Build}"
                : "unknown";

            var tracking = await _tracking.GetAllTrackingAsync();
            var config = await _tracking.LoadDragZoneConfigAsync();
            var savedTags = await _savedTagService.GetAllSavedTagsAsync();
            var p4Tables = await ExportP4TablesAsync();

            var export = new ExportData
            {
                SchemaVersion = SchemaVersion,
                ExportedAt = DateTime.UtcNow.ToString("O"),
                AppVersion = versionStr,
                Tracking = tracking.Select(t => new TrackingEntry
                {
                    AnimeId = t.AnimeId,
                    Status = t.Status,
                    UpdatedAt = t.UpdatedAt,
                }).ToList(),
                DragZones = config,
                SavedTags = savedTags.Select(name => new SavedTagEntry { TagName = name }).ToList(),
                P4Tables = p4Tables,
            };

            return JsonSerializer.Serialize(export, JsonOptions);
        }


        /// <summary>
        /// 从 JSON 字符串导入追番数据。
        /// 使用单连接+单事务，失败时自动回滚，避免半导入状态。
        /// </summary>
        /// <returns>
        /// (导入的追番数, 配置项数, 标签收藏数, 行动中心记录数)
        /// </returns>
        /// <exception cref="InvalidDataException">当 SchemaVersion 不兼容或数据校验不通过时抛出。</exception>
        public async Task<(
            int trackingCount,
            int configCount,
            int tagCount,
            int actionCenterCount)> ImportAsync(string json)
        {
            var export = ParseAndValidate(json);

            using var connection = await _dbFactory.OpenAsync();
            using var transaction = connection.BeginTransaction();

            try
            {
                int trackingCount = 0;
                foreach (var entry in export.Tracking)
                {
                    if (entry.AnimeId <= 0) continue;
                    if (!IsValidTrackingStatus(entry.Status)) continue;

                    var normalizedTime = NormalizeTimestamp(entry.UpdatedAt);
                    using var cmd = connection.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = """
                        INSERT OR REPLACE INTO tracking (AnimeId, Status, UpdatedAt)
                        VALUES (@id, @status, @time)
                        """;
                    cmd.Parameters.AddWithValue("@id", entry.AnimeId);
                    cmd.Parameters.AddWithValue("@status", (int)entry.Status);
                    cmd.Parameters.AddWithValue("@time", normalizedTime);
                    await cmd.ExecuteNonQueryAsync();
                    trackingCount++;
                }

                int configCount = 0;
                if (export.DragZones is { Count: > 0 })
                {
                    var validZones = export.DragZones
                        .Where(IsValidDragZone)
                        .Take(MaxDragZones)
                        .ToList();
                    if (validZones.Count > 0)
                    {
                        var zonesJson = JsonSerializer.Serialize(validZones, ConfigJsonOptions);
                        using var cmd = connection.CreateCommand();
                        cmd.Transaction = transaction;
                        cmd.CommandText = "INSERT OR REPLACE INTO config (Key, Value) VALUES ('drag_zones', @value)";
                        cmd.Parameters.AddWithValue("@value", zonesJson);
                        await cmd.ExecuteNonQueryAsync();
                        configCount = validZones.Count;
                    }
                }

                int tagCount = 0;
                var seenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (export.SavedTags is { Count: > 0 })
                {
                    foreach (var st in export.SavedTags)
                    {
                        var tagName = st.TagName?.Trim() ?? "";
                        if (tagName.Length == 0 || tagName.Length > MaxTagLength) continue;
                        if (!seenTags.Add(tagName)) continue;

                        using var cmd = connection.CreateCommand();
                        cmd.Transaction = transaction;
                        cmd.CommandText = "INSERT OR IGNORE INTO saved_tags (TagName) VALUES (@tag)";
                        cmd.Parameters.AddWithValue("@tag", tagName);
                        await cmd.ExecuteNonQueryAsync();
                        tagCount++;
                    }
                }

                var actionCenterCount = 0;
                if (export.P4Tables is not null)
                {
                    actionCenterCount = await ImportP4TablesAsync(
                        connection,
                        transaction,
                        export.P4Tables);
                }

                transaction.Commit();
                return (
                    trackingCount,
                    configCount,
                    tagCount,
                    actionCenterCount);
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }


        /// <summary>
        /// 仅预览导入数据，不实际写入。与 ImportAsync 共用同一套校验逻辑。
        /// </summary>
        public static ExportData? Preview(string json)
        {
            try
            {
                return ParseAndValidate(json);
            }
            catch (InvalidDataException)
            {
                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }


        // ======== 校验与归一化辅助 ========

        private const int MaxDragZones = 20;
        private const int MaxTagLength = 100;
        private const int MaxTrackingRecords = 10000;
        private const int MaxP4Records = 100000;
        private const int MaxJsonDepth = 32;

        /// <summary>反序列化并校验导入数据的 SchemaVersion 和基本结构。</summary>
        private static ExportData ParseAndValidate(string json)
        {
            // 校验 JSON 字符串长度（先于反序列化）
            if (json.Length > 10 * 1024 * 1024)
                throw new InvalidDataException("导入文件超过 10 MB 上限。");

            var options = new JsonSerializerOptions(JsonOptions)
            {
                MaxDepth = MaxJsonDepth,
                AllowTrailingCommas = false,
            };

            ExportData? export;
            try
            {
                export = JsonSerializer.Deserialize<ExportData>(json, options);
            }
            catch (JsonException ex) when (ex.Path is null && ex.StackTrace?.Contains("MaxDepth") == true)
            {
                throw new InvalidDataException("导入文件 JSON 嵌套过深。");
            }

            if (export?.Tracking == null)
                throw new InvalidDataException("导入文件格式无效");

            if (export.SchemaVersion <= 0 || export.SchemaVersion > SchemaVersion)
                throw new InvalidDataException(
                    $"不支持的导出文件版本：{export.SchemaVersion}。当前版本：{SchemaVersion}。");

            if (export.Tracking.Count > MaxTrackingRecords)
                throw new InvalidDataException(
                    $"追番记录数量 {export.Tracking.Count} 超过上限 {MaxTrackingRecords}。");

            var p4RecordCount =
                export.P4Tables?.Values.Sum(rows => rows.Count) ?? 0;
            if (p4RecordCount > MaxP4Records)
                throw new InvalidDataException(
                    $"行动中心记录数量 {p4RecordCount} 超过上限 {MaxP4Records}。");

            return export;
        }

        private async Task<Dictionary<string, List<Dictionary<string, string?>>>>
            ExportP4TablesAsync()
        {
            using var connection = await _dbFactory.OpenAsync();
            var result = new Dictionary<
                string,
                List<Dictionary<string, string?>>>(StringComparer.Ordinal);
            foreach (var table in P4TableColumns)
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    $"SELECT {string.Join(", ", table.Value)} FROM {table.Key}";
                var rows = new List<Dictionary<string, string?>>();
                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var row = new Dictionary<string, string?>(
                        StringComparer.Ordinal);
                    for (var index = 0; index < table.Value.Length; index++)
                    {
                        row[table.Value[index]] = reader.IsDBNull(index)
                            ? null
                            : Convert.ToString(
                                reader.GetValue(index),
                                CultureInfo.InvariantCulture);
                    }

                    rows.Add(row);
                }

                result[table.Key] = rows;
            }

            return result;
        }

        private static async Task<int> ImportP4TablesAsync(
            Microsoft.Data.Sqlite.SqliteConnection connection,
            Microsoft.Data.Sqlite.SqliteTransaction transaction,
            IReadOnlyDictionary<
                string,
                List<Dictionary<string, string?>>> tables)
        {
            var importedCount = 0;
            foreach (var table in P4TableColumns)
            {
                if (!tables.TryGetValue(table.Key, out var rows)
                    || rows.Count == 0)
                {
                    continue;
                }

                var parameterNames = table.Value
                    .Select((_, index) => $"@value{index}")
                    .ToArray();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"""
                    INSERT OR REPLACE INTO {table.Key}
                        ({string.Join(", ", table.Value)})
                    VALUES
                        ({string.Join(", ", parameterNames)})
                    """;
                foreach (var parameterName in parameterNames)
                {
                    command.Parameters.Add(
                        new Microsoft.Data.Sqlite.SqliteParameter(
                            parameterName,
                            DBNull.Value));
                }

                foreach (var row in rows)
                {
                    for (var index = 0; index < table.Value.Length; index++)
                    {
                        row.TryGetValue(table.Value[index], out var value);
                        command.Parameters[index].Value =
                            value ?? (object)DBNull.Value;
                    }

                    importedCount += await command.ExecuteNonQueryAsync();
                }
            }

            return importedCount;
        }

        private static bool IsValidTrackingStatus(AnimeTrackingStatus status)
        {
            if (!Enum.IsDefined(typeof(AnimeTrackingStatus), status))
                return false;
            return status != AnimeTrackingStatus.None;
        }

        private static bool IsValidDragZone(DragZoneConfig zone)
        {
            if (zone.XPercent < 0 || zone.XPercent > 1) return false;
            if (zone.YPercent < 0 || zone.YPercent > 1) return false;
            if (zone.WidthPercent < 0.08 || zone.WidthPercent > 0.6) return false;
            if (zone.HeightPercent < 0.08 || zone.HeightPercent > 0.6) return false;
            if (zone.XPercent + zone.WidthPercent > 1) return false;
            if (zone.YPercent + zone.HeightPercent > 1) return false;
            if (!Enum.IsDefined(typeof(DragAction), zone.Action)) return false;
            return true;
        }

        /// <summary>归一化时间戳。非法或空字符串使用当前 UTC 时间。</summary>
        private static string NormalizeTimestamp(string? timestamp)
        {
            if (string.IsNullOrWhiteSpace(timestamp))
                return DateTime.UtcNow.ToString("O");

            if (DateTime.TryParse(timestamp, out var dt))
                return dt.ToString("O");

            return DateTime.UtcNow.ToString("O");
        }
    }



    // ======== 导出数据模型 ========

    public class ExportData
    {
        public int SchemaVersion { get; set; }
        public string ExportedAt { get; set; } = "";
        public string AppVersion { get; set; } = "";
        public List<TrackingEntry> Tracking { get; set; } = new();
        public List<DragZoneConfig>? DragZones { get; set; }
        public List<SavedTagEntry>? SavedTags { get; set; }
        public Dictionary<string, List<Dictionary<string, string?>>>? P4Tables
        {
            get;
            set;
        }
    }

    public class TrackingEntry
    {
        public int AnimeId { get; set; }
        public AniMeido.Contracts.Models.AnimeTrackingStatus Status { get; set; }
        public string UpdatedAt { get; set; } = "";
    }

    public class SavedTagEntry
    {
        public string TagName { get; set; } = "";
    }
}
