using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AniMeido.Plugin.Player.Sources.Subscriptions;

internal sealed class GitHubSourceReader
{
    private readonly HttpClient _httpClient;

    public GitHubSourceReader(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<GitHubSourceFile>> ReadAsync(
        string url,
        SourceSubscriptionKind kind,
        CancellationToken cancellationToken)
    {
        var location = Parse(url);
        var branch = location.Branch
            ?? await GetDefaultBranchAsync(location, cancellationToken);
        var root = kind == SourceSubscriptionKind.EasyBangumi
            ? ResolveRoot(location.Subpath, "inner_source")
            : ResolveRoot(location.Subpath, "subs/web");
        var treeUri = new Uri(
            $"https://api.github.com/repos/{location.Owner}/{location.Repository}"
            + $"/git/trees/{Uri.EscapeDataString(branch)}?recursive=1");
        using var treeRequest = CreateRequest(treeUri);
        using var treeResponse = await _httpClient.SendAsync(
            treeRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        treeResponse.EnsureSuccessStatusCode();
        await using var treeStream = await treeResponse.Content.ReadAsStreamAsync(
            cancellationToken);
        var tree = await JsonSerializer.DeserializeAsync<GitHubTreeResponse>(
            treeStream,
            cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("GitHub 返回了空的文件树。");
        if (tree.Truncated)
        {
            throw new InvalidDataException(
                "GitHub 文件树被截断，无法安全刷新该订阅。");
        }

        var extension = kind == SourceSubscriptionKind.EasyBangumi
            ? ".js"
            : ".json";
        var paths = tree.Tree
            .Where(item => string.Equals(item.Type, "blob", StringComparison.Ordinal))
            .Select(item => item.Path)
            .Where(path => path.StartsWith(
                root.TrimEnd('/') + "/",
                StringComparison.Ordinal))
            .Where(path => path.EndsWith(
                extension,
                StringComparison.OrdinalIgnoreCase))
            .Where(path => kind != SourceSubscriptionKind.EasyBangumi
                || string.Equals(
                    Path.GetDirectoryName(path)?.Replace('\\', '/'),
                    root.TrimEnd('/'),
                    StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (paths.Length == 0)
        {
            throw new InvalidDataException(
                $"GitHub 仓库中未找到 {root}/ 下的源文件。");
        }

        using var concurrency = new SemaphoreSlim(6, 6);
        var files = await Task.WhenAll(paths.Select(async path =>
        {
            await concurrency.WaitAsync(cancellationToken);
            try
            {
                var rawUri = new Uri(
                    $"https://raw.githubusercontent.com/{location.Owner}/"
                    + $"{location.Repository}/{branch}/{path}");
                using var request = CreateRequest(rawUri);
                using var response = await _httpClient.SendAsync(
                    request,
                    cancellationToken);
                response.EnsureSuccessStatusCode();
                return new GitHubSourceFile(
                    path,
                    await response.Content.ReadAsStringAsync(cancellationToken));
            }
            finally
            {
                concurrency.Release();
            }
        }));
        return files;
    }

    private async Task<string> GetDefaultBranchAsync(
        GitHubRepositoryLocation location,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(
            $"https://api.github.com/repos/{location.Owner}/{location.Repository}");
        using var request = CreateRequest(uri);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);
        return document.RootElement.GetProperty("default_branch").GetString()
            ?? throw new InvalidDataException("GitHub 仓库没有默认分支。");
    }

    private static HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(
            new ProductInfoHeaderValue("AniMeido", "1.0"));
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return request;
    }

    private static GitHubRepositoryLocation Parse(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(
                uri.Host,
                "github.com",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("首版源订阅只支持 github.com URL。");
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            throw new InvalidDataException("GitHub 仓库 URL 不完整。");
        }

        string? branch = null;
        string? subpath = null;
        if (segments.Length >= 4
            && string.Equals(segments[2], "tree", StringComparison.Ordinal))
        {
            branch = segments[3];
            subpath = segments.Length > 4
                ? string.Join('/', segments.Skip(4))
                : null;
        }

        return new GitHubRepositoryLocation(
            segments[0],
            segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                ? segments[1][..^4]
                : segments[1],
            branch,
            subpath);
    }

    private static string ResolveRoot(string? subpath, string defaultRoot)
        => string.IsNullOrWhiteSpace(subpath)
            ? defaultRoot
            : subpath.Trim('/');

    private sealed record GitHubRepositoryLocation(
        string Owner,
        string Repository,
        string? Branch,
        string? Subpath);

    private sealed class GitHubTreeResponse
    {
        [JsonPropertyName("truncated")]
        public bool Truncated { get; set; }

        [JsonPropertyName("tree")]
        public List<GitHubTreeItem> Tree { get; set; } = [];
    }

    private sealed class GitHubTreeItem
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;
    }
}

internal sealed record GitHubSourceFile(string Path, string Content);
