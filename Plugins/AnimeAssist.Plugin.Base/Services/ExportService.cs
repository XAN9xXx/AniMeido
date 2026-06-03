using AniMeido.Contracts.Models;
using System.Text.Json;
using AniMeido.Plugin.Base.Models;

namespace AniMeido.Plugin.Base.Services
{
    /// <summary>
    /// 追番数据 JSON 导出/导入服务。
    /// 导出内容：用户标记的番剧状态和拖放配置。
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

        public const int SchemaVersion = 2;



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
            };

            return JsonSerializer.Serialize(export, JsonOptions);
        }


        /// <summary>
        /// 从 JSON 字符串导入追番数据。
        /// 使用单连接+单事务，失败时自动回滚，避免半导入状态。
        /// </summary>
        /// <returns>(导入的追番数, 导入的配置项数, 导入的标签收藏数)</returns>
        /// <exception cref="InvalidDataException">当 SchemaVersion 不兼容或数据校验不通过时抛出。</exception>
        public async Task<(int trackingCount, int configCount, int tagCount)> ImportAsync(string json)
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

                transaction.Commit();
                return (trackingCount, configCount, tagCount);
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

            return export;
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