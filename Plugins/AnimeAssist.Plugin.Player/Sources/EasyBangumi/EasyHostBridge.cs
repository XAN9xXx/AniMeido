using AniMeido.Plugin.Player.Sources.Web;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AniMeido.Plugin.Player.Sources.EasyBangumi;

internal sealed class EasyHostBridge
{
    private const string DefaultUserAgentValue =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 "
        + "(KHTML, like Gecko) Chrome/124.0 Safari/537.36";
    private readonly HttpClient _httpClient;
    private readonly WebMediaResolver _webResolver;
    private readonly string _sourceId;
    private readonly IReadOnlyDictionary<string, string> _preferences;
    private readonly CancellationToken _cancellationToken;
    private IReadOnlyDictionary<string, string>? _lastResolvedHeaders;

    public EasyHostBridge(
        HttpClient httpClient,
        WebMediaResolver webResolver,
        string sourceId,
        IReadOnlyDictionary<string, string> preferences,
        CancellationToken cancellationToken)
    {
        _httpClient = httpClient;
        _webResolver = webResolver;
        _sourceId = sourceId;
        _preferences = preferences;
        _cancellationToken = cancellationToken;
    }

    public string DefaultUserAgent => DefaultUserAgentValue;

    public IReadOnlyDictionary<string, string>? LastResolvedHeaders
        => _lastResolvedHeaders;

    public string GetPreference(string key, string fallback)
        => _preferences.GetValueOrDefault(key) ?? fallback;

    public EasyHtmlDocument ParseHtml(string html, string? baseUrl = null)
        => new(html, baseUrl);

    public EasyHttpResponse Execute(
        string url,
        string method,
        string? headersJson,
        string? body)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(
            string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
                ? HttpMethod.Post
                : HttpMethod.Get,
            url);
        var headers = ParseHeaders(headersJson);
        foreach (var header in headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (!request.Headers.UserAgent.Any())
        {
            request.Headers.UserAgent.ParseAdd(DefaultUserAgentValue);
        }

        if (request.Method == HttpMethod.Post)
        {
            request.Content = new StringContent(
                body ?? string.Empty,
                System.Text.Encoding.UTF8,
                "application/x-www-form-urlencoded");
        }

        using var response = _httpClient.Send(
            request,
            HttpCompletionOption.ResponseContentRead,
            _cancellationToken);
        var content = response.Content.ReadAsStringAsync(_cancellationToken)
            .GetAwaiter()
            .GetResult();
        return new EasyHttpResponse(
            response.IsSuccessStatusCode,
            (int)response.StatusCode,
            content);
    }

    public EasyVideoResult ResolveVideo(
        string url,
        string? userAgent,
        string? headersJson,
        int timeout,
        string? actionScript,
        bool useLegacyParser)
    {
        var headers = ParseHeaders(headersJson);
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            headers["User-Agent"] = userAgent;
        }

        var result = _webResolver.ResolveAsync(
            new WebResolutionRequest(
                _sourceId,
                new Uri(url),
                @"https?://[^\s""'<>\\]+?(?:\.m3u8|\.mp4)(?:\?[^\s""'<>\\]*)?",
                EnableNestedUrl: true,
                NestedUrlPattern: @"https?://[^\s""'<>\\]+",
                ScanDomMediaUrls: true,
                ScanInlineScriptUrls: true,
                headers,
                Cookies: null,
                SourceDeclaredTimeout: TimeSpan.FromMilliseconds(
                    Math.Clamp(timeout, 5_000, 120_000)),
                ActionScript: actionScript,
                UseLegacyParser: useLegacyParser),
            _cancellationToken)
            .GetAwaiter()
            .GetResult();
        _lastResolvedHeaders = HeaderNormalizer.Merge(result.Headers);
        return new EasyVideoResult(
            result.Uri.AbsoluteUri,
            result.Uri.AbsolutePath.EndsWith(
                ".m3u8",
                StringComparison.OrdinalIgnoreCase),
            _lastResolvedHeaders);
    }

    public string LoadPage(
        string url,
        bool interactive)
        => _webResolver.LoadPageHtmlAsync(
            new Uri(url),
            interactive,
            _cancellationToken)
            .GetAwaiter()
            .GetResult();

    public string ResolveUrl(string baseUrl, string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            return absolute.AbsoluteUri;
        }

        return new Uri(new Uri(baseUrl), value).AbsoluteUri;
    }

    private static Dictionary<string, string> ParseHeaders(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
        }

        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(
            json);
        return HeaderNormalizer.Merge(parsed);
    }
}

internal sealed class EasyHttpResponse
{
    private readonly bool _isSuccessful;
    private readonly int _statusCode;
    private readonly string _content;

    public EasyHttpResponse(
        bool isSuccessful,
        int statusCode,
        string content)
    {
        _isSuccessful = isSuccessful;
        _statusCode = statusCode;
        _content = content;
    }

    public bool isSuccessful() => _isSuccessful;

    public int code() => _statusCode;

    public EasyHttpBody body() => new(_content);

    public void close()
    {
    }
}

internal sealed class EasyHttpBody
{
    private readonly string _content;

    public EasyHttpBody(string content)
    {
        _content = content;
    }

    public string @string() => _content;
}

internal sealed record EasyVideoResult(
    string url,
    bool isM3u8,
    IReadOnlyDictionary<string, string> headers);
