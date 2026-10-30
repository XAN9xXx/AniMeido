using System.Net;
using System.Text.Json;
using AniMeido.App.Services;

namespace AniMeido.Tests;

public sealed class UpdateServiceTests
{
    private const string DownloadUrl = "https://example.invalid/update.exe";

    [Fact]
    public async Task CheckForUpdate_CallerCancellationDoesNotBecomeNetworkFailure()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new CancellationHandler();
        using var client = new HttpClient(handler);
        var service = CreateService(client, new Version(2, 1, 2));
        var checking = service.CheckForUpdateAsync(cancellation.Token);
        try
        {
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checking.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            cancellation.Cancel();
            await Task.WhenAny(checking).WaitAsync(TimeSpan.FromSeconds(5));
            await ((Task)checking).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    private sealed class CancellationHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
    }

    [Fact]
    public async Task CheckForUpdate_BelowMinimumVersion_PreservesUpdateDetails()
    {
        using var client = new HttpClient(new ManifestHandler("""
            {
              "latestVersion": "999.1.0",
              "downloadUrl": "https://example.invalid/update.exe",
              "releaseNotes": "修复更新入口",
              "minAppVersion": "999.0.0"
            }
            """));
        var service = CreateService(client, new Version(2, 1, 2, 0));

        var result = await service.CheckForUpdateAsync();

        Assert.Equal(DownloadUrl, result.DownloadUrl);
        Assert.Equal(UpdateCheckStatus.UpdateRequired, result.Status);
        Assert.Equal("999.1.0", result.LatestVersion);
        Assert.Equal("修复更新入口", result.ReleaseNotes);
    }

    [Theory]
    [InlineData("2.1.1", UpdateCheckStatus.NoUpdate)]
    [InlineData("2.1.2", UpdateCheckStatus.NoUpdate)]
    [InlineData("2.1.2.0", UpdateCheckStatus.NoUpdate)]
    [InlineData("2.1.3", UpdateCheckStatus.UpdateAvailable)]
    public async Task CheckForUpdate_ComparedVersions_ReturnExpectedStatus(string latest, UpdateCheckStatus expected)
    {
        var result = await CheckAsync(Manifest(latest));

        Assert.Equal(expected, result.Status);
        Assert.Equal(latest, result.LatestVersion);
        Assert.Null(result.FailureReason);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"downloadUrl\":\"https://example.invalid/update.exe\"}")]
    [InlineData("{\"latestVersion\":null,\"downloadUrl\":\"https://example.invalid/update.exe\"}")]
    [InlineData("{\"latestVersion\":\"invalid\",\"downloadUrl\":\"https://example.invalid/update.exe\"}")]
    [InlineData("{\"latestVersion\":12,\"downloadUrl\":\"https://example.invalid/update.exe\"}")]
    [InlineData("{\"latestVersion\":\"2.2.0\"}")]
    public async Task CheckForUpdate_InvalidManifest_ReportsReason(string json)
    {
        var result = await CheckAsync(json);

        Assert.Equal(UpdateCheckStatus.InvalidManifest, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
        Assert.Null(result.DownloadUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/update.exe")]
    [InlineData("//example.invalid/update.exe")]
    [InlineData("http://example.invalid/update.exe")]
    [InlineData("ftp://example.invalid/update.exe")]
    [InlineData("file:///C:/update.exe")]
    [InlineData("javascript:alert(1)")]
    public async Task CheckForUpdate_NonAbsoluteHttpsDownload_IsInvalidManifest(string? url)
    {
        var result = await CheckAsync(Manifest(downloadUrl: url));

        Assert.Equal(UpdateCheckStatus.InvalidManifest, result.Status);
        Assert.Contains("downloadUrl", result.FailureReason);
        Assert.Null(result.DownloadUrl);
    }

    [Fact]
    public async Task CheckForUpdate_UnknownCurrentVersion_IsNotManifestFailure()
    {
        using var client = new HttpClient(new ManifestHandler(Manifest()));
        var result = await CreateService(client, null).CheckForUpdateAsync();

        Assert.Equal(UpdateCheckStatus.UnknownCurrentVersion, result.Status);
        Assert.Contains("当前程序版本", result.FailureReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckForUpdate_RequestFailure_IsNetworkError(bool timeout)
    {
        using var client = new HttpClient(new FailingHandler(timeout));
        var result = await CreateService(client, new Version(2, 1, 2, 0)).CheckForUpdateAsync();

        Assert.Equal(UpdateCheckStatus.NetworkError, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
        Assert.Null(result.DownloadUrl);
    }

    [Fact]
    public async Task CheckForUpdate_UnsuccessfulHttpStatus_IsNetworkError()
    {
        using var client = new HttpClient(new ManifestHandler("unavailable", HttpStatusCode.ServiceUnavailable));
        var result = await CreateService(client, new Version(2, 1, 2, 0)).CheckForUpdateAsync();

        Assert.Equal(UpdateCheckStatus.NetworkError, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("-1.0")]
    [InlineData("999999999999999.0")]
    [InlineData("1")]
    public async Task CheckForUpdate_InvalidMinimumVersion_IsIgnored(string? minimum)
    {
        var result = await CheckAsync(Manifest(minimumVersion: minimum));

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal(DownloadUrl, result.DownloadUrl);
    }

    [Fact]
    public async Task CheckForUpdate_ValidOptionalMetadata_IsReturnedForRequiredUpdate()
    {
        var sha256 = new string('A', 32) + new string('f', 32);
        var json = Manifest(minimumVersion: "2.2.0", optionalJson:
            $"\"sha256\":\"{sha256}\",\"size\":123456789,\"releaseDate\":\"2026-10-03\"");

        var result = await CheckAsync(json);

        Assert.Equal(UpdateCheckStatus.UpdateRequired, result.Status);
        Assert.Equal(DownloadUrl, result.DownloadUrl);
        Assert.Equal(sha256, result.Sha256);
        Assert.Equal(123456789L, result.Size);
        Assert.Equal(new DateOnly(2026, 10, 3), result.ReleaseDate);
    }

    [Fact]
    public async Task CheckForUpdate_OldManifest_OptionalMetadataRemainsNull()
    {
        var result = await CheckAsync("""
            {"latestVersion":"2.2.0","downloadUrl":"https://example.invalid/update.exe","releaseNotes":"旧清单"}
            """);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Null(result.Sha256);
        Assert.Null(result.Size);
        Assert.Null(result.ReleaseDate);
        Assert.Equal("旧清单", result.ReleaseNotes);
    }

    [Theory]
    [InlineData("sha256", "null")]
    [InlineData("sha256", "123")]
    [InlineData("sha256", "{}")]
    [InlineData("sha256", "[]")]
    [InlineData("sha256", "\"short\"")]
    [InlineData("sha256", "\"gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg\"")]
    [InlineData("sha256", "\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"")]
    [InlineData("size", "null")]
    [InlineData("size", "\"123\"")]
    [InlineData("size", "-1")]
    [InlineData("size", "1.5")]
    [InlineData("size", "9223372036854775808")]
    [InlineData("size", "true")]
    [InlineData("size", "[]")]
    [InlineData("releaseDate", "null")]
    [InlineData("releaseDate", "20261003")]
    [InlineData("releaseDate", "{}")]
    [InlineData("releaseDate", "\"2026/10/03\"")]
    [InlineData("releaseDate", "\"2026-02-30\"")]
    [InlineData("releaseDate", "\"2026-1-3\"")]
    [InlineData("releaseDate", "\"2026-10-03T00:00:00Z\"")]
    public async Task CheckForUpdate_InvalidOptionalField_IgnoresOnlyThatField(string field, string value)
    {
        var metadata = new Dictionary<string, JsonElement>
        {
            ["sha256"] = JsonSerializer.SerializeToElement(new string('a', 64)),
            ["size"] = JsonSerializer.SerializeToElement(100L),
            ["releaseDate"] = JsonSerializer.SerializeToElement("2026-10-03"),
        };
        using var document = JsonDocument.Parse(value);
        metadata[field] = document.RootElement.Clone();
        var optionalJson = string.Join(",", metadata.Select(pair => $"\"{pair.Key}\":{pair.Value.GetRawText()}"));

        var result = await CheckAsync(Manifest(optionalJson: optionalJson));

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal(DownloadUrl, result.DownloadUrl);
        Assert.Equal(field == "sha256" ? null : new string('a', 64), result.Sha256);
        Assert.Equal(field == "size" ? (long?)null : 100L, result.Size);
        Assert.Equal(field == "releaseDate" ? (DateOnly?)null : new DateOnly(2026, 10, 3), result.ReleaseDate);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(long.MaxValue)]
    public async Task CheckForUpdate_NonNegativeInt64Size_IsValid(long size)
    {
        var result = await CheckAsync(Manifest(optionalJson: $"\"size\":{size}"));

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal(size, result.Size);
    }

    [Fact]
    public async Task CheckForUpdate_FieldNames_AreCaseInsensitive()
    {
        var result = await CheckAsync("""
            {"LATESTVERSION":"2.2.0","DOWNLOADURL":"HTTPS://example.invalid/update.exe","SIZE":42,"RELEASEDATE":"2024-02-29"}
            """);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal(42L, result.Size);
        Assert.Equal(new DateOnly(2024, 2, 29), result.ReleaseDate);
    }

    private static async Task<UpdateService.UpdateCheckResult> CheckAsync(string json)
    {
        using var client = new HttpClient(new ManifestHandler(json));
        return await CreateService(client, new Version(2, 1, 2, 0)).CheckForUpdateAsync();
    }

    private static UpdateService CreateService(HttpClient client, Version? current)
        => new(new StubClientFactory(client), "https://example.invalid/version.json", current);

    private static string Manifest(
        string latest = "2.2.0",
        string? downloadUrl = DownloadUrl,
        string? minimumVersion = "1.0.0",
        string? optionalJson = null)
    {
        var json = JsonSerializer.Serialize(new
        {
            latestVersion = latest,
            downloadUrl,
            releaseNotes = "更新说明",
            minAppVersion = minimumVersion,
        });
        return optionalJson is null ? json : json[..^1] + "," + optionalJson + "}";
    }

    private sealed class StubClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ManifestHandler(string json, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json),
            });
    }

    private sealed class FailingHandler(bool timeout) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(timeout
                ? new TaskCanceledException("fixture timeout")
                : new HttpRequestException("fixture offline"));
    }
}
