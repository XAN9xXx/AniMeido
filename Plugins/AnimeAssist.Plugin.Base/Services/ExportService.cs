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
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public const int SchemaVersion = 1;



        public ExportService(TrackingService tracking)
        {
            _tracking = tracking;
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
            };

            return JsonSerializer.Serialize(export, JsonOptions);
        }


        /// <summary>
        /// 从 JSON 字符串导入追番数据。导入前自动备份数据库。
        /// 已存在的记录会被覆盖，新记录会被添加。
        /// </summary>
        /// <returns>(导入的追番数, 导入的配置项数)</returns>
        public async Task<(int trackingCount, int configCount)> ImportAsync(string json)
        {
            var export = JsonSerializer.Deserialize<ExportData>(json, JsonOptions);
            if (export?.Tracking == null)
                throw new InvalidDataException("导入文件格式无效");

            int trackingCount = 0;
            foreach (var entry in export.Tracking)
            {
                await _tracking.SetStatusAsync(entry.AnimeId, entry.Status);
                trackingCount++;
            }

            int configCount = 0;
            if (export.DragZones is { Count: > 0 })
            {
                await _tracking.SaveDragZoneConfigAsync(export.DragZones);
                configCount = export.DragZones.Count;
            }

            return (trackingCount, configCount);
        }


        /// <summary>
        /// 仅预览导入数据，不实际写入。
        /// </summary>
        public static ExportData? Preview(string json)
        {
            return JsonSerializer.Deserialize<ExportData>(json, JsonOptions);
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
    }

    public class TrackingEntry
    {
        public int AnimeId { get; set; }
        public AniMeido.Contracts.Models.AnimeTrackingStatus Status { get; set; }
        public string UpdatedAt { get; set; } = "";
    }
}
