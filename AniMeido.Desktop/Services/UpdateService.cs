using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace AniMeido.App.Services
{
    public enum UpdateCheckStatus
    {
        NoUpdate,
        UpdateAvailable,
        UpdateRequired,
        NetworkError,
        InvalidManifest,
        UnknownCurrentVersion
    }

    public class UpdateService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _manifestUrl;
        private readonly Version? _currentVersion;

        internal record UpdateCheckResult(
            UpdateCheckStatus Status,
            string? LatestVersion,
            string? DownloadUrl,
            string? ReleaseNotes,
            string? Sha256 = null,
            long? Size = null,
            DateOnly? ReleaseDate = null,
            string? FailureReason = null);

        // 可选元数据使用 JsonElement，类型错误也只忽略该字段，不让反序列化拒绝整份清单。
        private record VersionManifest(
            string? LatestVersion,
            string? DownloadUrl,
            string? ReleaseNotes,
            string? MinAppVersion,
            JsonElement Sha256,
            JsonElement Size,
            JsonElement ReleaseDate);

        public UpdateService(IHttpClientFactory httpClientFactory, string manifestUrl)
            : this(httpClientFactory, manifestUrl, Assembly.GetEntryAssembly()?.GetName().Version)
        {
        }

        internal UpdateService(
            IHttpClientFactory httpClientFactory,
            string manifestUrl,
            Version? currentVersion)
        {
            _httpClientFactory = httpClientFactory;
            _manifestUrl = manifestUrl;
            _currentVersion = currentVersion;
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        internal async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
        {
            using var client = _httpClientFactory.CreateClient();
            string json;
            try
            {
                json = await client.GetStringAsync(_manifestUrl, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                return Failure(UpdateCheckStatus.NetworkError, $"无法获取更新清单：{ex.Message}");
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Failure(UpdateCheckStatus.NetworkError, "获取更新清单超时或请求已取消，请检查网络连接后重试。");
            }

            VersionManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<VersionManifest>(json, JsonOptions);
            }
            catch (JsonException)
            {
                return Failure(UpdateCheckStatus.InvalidManifest, "更新清单为空、JSON 无法解析或字段类型不正确。");
            }

            if (manifest is null)
                return Failure(UpdateCheckStatus.InvalidManifest, "更新清单为空。");
            if (!Version.TryParse(manifest.LatestVersion, out var latestVersion))
                return Failure(UpdateCheckStatus.InvalidManifest, "更新清单的 latestVersion 缺失或版本号无法解析。");
            if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var downloadUri)
                || downloadUri.Scheme != Uri.UriSchemeHttps
                || string.IsNullOrEmpty(downloadUri.Host))
                return Failure(UpdateCheckStatus.InvalidManifest, "更新清单的 downloadUrl 必须是绝对 HTTPS 地址。");
            if (_currentVersion is null)
                return Failure(UpdateCheckStatus.UnknownCurrentVersion, "无法读取当前程序版本，无法判断是否需要更新。");

            var status = UpdateCheckStatus.NoUpdate;
            // 保持旧清单兼容：无效的 minAppVersion 不参与最低版本判断。
            if (Version.TryParse(manifest.MinAppVersion, out var minimumVersion)
                && _currentVersion < minimumVersion)
                status = UpdateCheckStatus.UpdateRequired;
            else if (latestVersion > _currentVersion)
                status = UpdateCheckStatus.UpdateAvailable;

            return new UpdateCheckResult(
                status,
                manifest.LatestVersion,
                manifest.DownloadUrl,
                manifest.ReleaseNotes,
                ReadSha256(manifest.Sha256),
                ReadSize(manifest.Size),
                ReadReleaseDate(manifest.ReleaseDate));
        }

        private static UpdateCheckResult Failure(UpdateCheckStatus status, string reason)
            => new(status, null, null, null, FailureReason: reason);

        private static string? ReadSha256(JsonElement value)
        {
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return text is { Length: 64 } && text.All(Uri.IsHexDigit) ? text : null;
        }

        private static long? ReadSize(JsonElement value)
            => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var size) && size >= 0
                ? size : null;

        private static DateOnly? ReadReleaseDate(JsonElement value)
            => value.ValueKind == JsonValueKind.String
                && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date)
                ? date : null;
    }
}
