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

    public Task<IReadOnlyList<GitHubSourceFile>> ReadAsync(
        string url,
        SourceSubscriptionKind kind,
        CancellationToken cancellationToken)
        => ReadAsync(url, kind, null, cancellationToken);

    internal async Task<IReadOnlyList<GitHubSourceFile>> ReadAsync(
        string url,
        SourceSubscriptionKind kind,
        IReadOnlyDictionary<string, string>? knownVersions,
        CancellationToken cancellationToken)
    {
        var location = Parse(url);
        var branch = location.Branch
            ?? await GetDefaultBranchAsync(location, cancellationToken).ConfigureAwait(false);
        var revision = await GetRevisionAsync(location, branch, cancellationToken).ConfigureAwait(false);
        var root = kind == SourceSubscriptionKind.EasyBangumi
            ? ResolveRoot(location.Subpath, "inner_source")
            : ResolveRoot(location.Subpath, "subs/web");
        var treeUri = new Uri(
            $"https://api.github.com/repos/{location.Owner}/{location.Repository}"
            + $"/git/trees/{Uri.EscapeDataString(revision.TreeId)}?recursive=1");
        using var treeRequest = CreateRequest(treeUri);
        using var treeResponse = await _httpClient.SendAsync(
            treeRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        treeResponse.EnsureSuccessStatusCode();
        await using var treeStream = await treeResponse.Content.ReadAsStreamAsync(
            cancellationToken).ConfigureAwait(false);
        var tree = await JsonSerializer.DeserializeAsync<GitHubTreeResponse>(
            treeStream,
            cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("GitHub 返回了空的文件树。");
        if (tree.Truncated)
        {
            throw new InvalidDataException(
                "GitHub 文件树被截断，无法安全刷新该订阅。");
        }

        var extension = kind == SourceSubscriptionKind.EasyBangumi
            ? ".js"
            : ".json";
        var entries = tree.Tree
            .Where(item => string.Equals(item.Type, "blob", StringComparison.Ordinal))
            .Where(item => item.Path.StartsWith(
                root.TrimEnd('/') + "/",
                StringComparison.Ordinal))
            .Where(item => item.Path.EndsWith(
                extension,
                StringComparison.OrdinalIgnoreCase))
            .Where(item => kind != SourceSubscriptionKind.EasyBangumi
                || string.Equals(
                    Path.GetDirectoryName(item.Path)?.Replace('\\', '/'),
                    root.TrimEnd('/'),
                    StringComparison.Ordinal))
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ToArray();
        if (entries.Length == 0)
        {
            throw new InvalidDataException(
                $"GitHub 仓库中未找到 {root}/ 下的源文件。");
        }

        var files = new GitHubSourceFile[entries.Length];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, entries.Length),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = 6,
                CancellationToken = cancellationToken,
            },
            async (index, token) =>
            {
                var entry = entries[index];
                if (!string.IsNullOrWhiteSpace(entry.Sha)
                    && knownVersions?.TryGetValue(entry.Path, out var previous) == true
                    && string.Equals(previous, entry.Sha, StringComparison.Ordinal))
                {
                    files[index] = new GitHubSourceFile(entry.Path, null, entry.Sha);
                    return;
                }

                var rawUri = new Uri(
                    $"https://raw.githubusercontent.com/{location.Owner}/"
                    + $"{location.Repository}/{Uri.EscapeDataString(revision.CommitId)}/{entry.Path}");
                using var request = CreateRequest(rawUri);
                using var response = await _httpClient.SendAsync(
                    request,
                    token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                files[index] = new GitHubSourceFile(
                    entry.Path,
                    await response.Content.ReadAsStringAsync(token).ConfigureAwait(false),
                    entry.Sha);
            }).ConfigureAwait(false);
        return files;
    }

    private async Task<RepositoryRevision> GetRevisionAsync(
        GitHubRepositoryLocation location,
        string reference,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(
            $"https://api.github.com/repos/{location.Owner}/{location.Repository}"
            + $"/commits/{Uri.EscapeDataString(reference)}?per_page=1");
        using var request = CreateRequest(uri);
        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
            stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var commitId = document.RootElement.GetProperty("sha").GetString()
            ?? throw new InvalidDataException("GitHub 提交信息缺少提交版本。");
        var treeId = document.RootElement.GetProperty("commit").GetProperty("tree").GetProperty("sha").GetString()
            ?? throw new InvalidDataException("GitHub 提交信息缺少文件树版本。");
        return new RepositoryRevision(commitId, treeId);
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
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
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

    private sealed record RepositoryRevision(string CommitId, string TreeId);

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

        [JsonPropertyName("sha")]
        public string? Sha { get; set; }
    }
}

internal sealed record GitHubSourceFile(string Path, string? Content, string? BlobId = null);
