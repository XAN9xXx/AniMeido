using System.Text.Json;
using System.Reflection;

namespace AniMeido.App.Services
{
    public class UpdateService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _manifestUrl;
        internal record UpdateCheckResult(bool HasUpdate, string? LatestVersion, string? DownloadUrl, string? ReleaseNotes);
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

        internal async Task<UpdateCheckResult?> CheckForUpdateAsync()
        {
            var client = _httpClientFactory.CreateClient();
            string json;

            try
            {
                json = await client.GetStringAsync(_manifestUrl).ConfigureAwait(false);
                VersionManifest? jsonVersionManifest = JsonSerializer.Deserialize<VersionManifest>(json, JsonOptions);
                var currentVersion = Assembly.GetEntryAssembly()?.GetName()?.Version;
                if (currentVersion == null) return null;
                if (jsonVersionManifest == null) return null;

                var latestVersion = new Version(jsonVersionManifest.LatestVersion);

                if (latestVersion > currentVersion) return new UpdateCheckResult(true, jsonVersionManifest.LatestVersion, jsonVersionManifest.DownloadUrl, jsonVersionManifest.ReleaseNotes);
                return new UpdateCheckResult(false, jsonVersionManifest.LatestVersion, null, null);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
