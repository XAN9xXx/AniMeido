using System.Text.Json;
using System.Reflection;

namespace AniMeido.App.Services
{
    public enum UpdateCheckStatus
    {
        NoUpdate,
        UpdateAvailable,
        NetworkError,
        InvalidManifest,
        IncompatibleClient
    }

    public class UpdateService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _manifestUrl;
        internal record UpdateCheckResult(UpdateCheckStatus Status, string? LatestVersion, string? DownloadUrl, string? ReleaseNotes);
        private record VersionManifest(string LatestVersion, string DownloadUrl, string? ReleaseNotes, string MinAppVersion);

        public UpdateService(IHttpClientFactory httpClientFactory, string manifestUrl)
        {
            _httpClientFactory = httpClientFactory;
            _manifestUrl = manifestUrl;
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        internal async Task<UpdateCheckResult> CheckForUpdateAsync()
        {
            var client = _httpClientFactory.CreateClient();
            string json;

            try
            {
                json = await client.GetStringAsync(_manifestUrl).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                return new UpdateCheckResult(UpdateCheckStatus.NetworkError, null, null, null);
            }
            catch (TaskCanceledException)
            {
                return new UpdateCheckResult(UpdateCheckStatus.NetworkError, null, null, null);
            }

            VersionManifest? jsonVersionManifest;
            try
            {
                jsonVersionManifest = JsonSerializer.Deserialize<VersionManifest>(json, JsonOptions);
            }
            catch (JsonException)
            {
                return new UpdateCheckResult(UpdateCheckStatus.InvalidManifest, null, null, null);
            }

            var currentVersion = Assembly.GetEntryAssembly()?.GetName()?.Version;
            if (currentVersion == null || jsonVersionManifest == null)
                return new UpdateCheckResult(UpdateCheckStatus.IncompatibleClient, null, null, null);

            Version latestVersion;
            try
            {
                latestVersion = new Version(jsonVersionManifest.LatestVersion);
            }
#pragma warning disable CA1031 // 版本解析失败视为无效清单
            catch (Exception)
            {
                return new UpdateCheckResult(UpdateCheckStatus.InvalidManifest, null, null, null);
            }
#pragma warning restore CA1031

            // 检查 MinAppVersion
            if (!string.IsNullOrEmpty(jsonVersionManifest.MinAppVersion))
            {
                try
                {
                    var minVersion = new Version(jsonVersionManifest.MinAppVersion);
                    if (currentVersion < minVersion)
                        return new UpdateCheckResult(UpdateCheckStatus.IncompatibleClient,
                            jsonVersionManifest.LatestVersion, null, null);
                }
                catch (FormatException)
                {
                    // MinAppVersion 格式异常，忽略
                }
            }

            if (latestVersion > currentVersion)
                return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, jsonVersionManifest.LatestVersion, jsonVersionManifest.DownloadUrl, jsonVersionManifest.ReleaseNotes);

            return new UpdateCheckResult(UpdateCheckStatus.NoUpdate, jsonVersionManifest.LatestVersion, null, null);
        }
    }
}
