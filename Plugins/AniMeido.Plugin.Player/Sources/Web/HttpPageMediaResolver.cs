using AniMeido.Plugin.Player.Diagnostics;

namespace AniMeido.Plugin.Player.Sources.Web;

/// <summary>拥有 HTTP 页面及一层嵌套页到候选媒体的读取，不管理浏览器、探测或解析预算。</summary>
internal sealed class HttpPageMediaResolver(HttpClient httpClient, PlaybackDiagnosticRecorder diagnostics)
{
    public async Task<WebResolvedMedia?> TryResolveAsync(
        WebResolutionRequest request,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            request.PageUri);
        HeaderNormalizer.AddToRequest(message, request.Headers, request.Cookies);
        diagnostics.Record(
            "http",
            "request",
            request.SourceId,
            request.PageUri,
            new Dictionary<string, object?>
            {
                ["method"] = "GET",
                ["phase"] = "page",
                ["headerNames"] = string.Join(
                    ",",
                    request.Headers.Keys.Order(StringComparer.OrdinalIgnoreCase)),
                ["hasCookie"] = !string.IsNullOrWhiteSpace(request.Cookies),
            });
        var requestStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        using var response = await httpClient.SendAsync(
            message,
            cancellationToken);
        diagnostics.Record(
            "http",
            "response",
            request.SourceId,
            request.PageUri,
            new Dictionary<string, object?>
            {
                ["phase"] = "page",
                ["status"] = (int)response.StatusCode,
                ["contentType"] =
                    response.Content.Headers.ContentType?.MediaType,
                ["contentLength"] =
                    response.Content.Headers.ContentLength,
                ["elapsedMs"] = System.Diagnostics.Stopwatch
                    .GetElapsedTime(requestStarted)
                    .TotalMilliseconds,
                ["redirectLocation"] = response.Headers.Location,
            });
        var failureKind = WebPageAccessEvaluator.Classify(
            response.StatusCode,
            WebPageInteractionKind.None);
        if (failureKind is not null)
        {
            throw new SourceResolutionException(
                failureKind.Value,
                $"源站返回 HTTP {(int)response.StatusCode}。",
                request.PageUri);
        }

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        diagnostics.Record(
            "http",
            "response-body",
            request.SourceId,
            request.PageUri,
            new Dictionary<string, object?>
            {
                ["phase"] = "page",
                ["characters"] = html.Length,
            },
            html);
        var effectivePageUri = response.RequestMessage?.RequestUri ?? request.PageUri;
        var resolved = WebMediaUrlParser.FindMedia(html, request, effectivePageUri);
        if (resolved is not null)
        {
            return new WebResolvedMedia(
                resolved,
                HeaderNormalizer.BuildMediaHeaders(request, effectivePageUri));
        }

        if (request.EnableNestedUrl
            && !string.IsNullOrWhiteSpace(request.NestedUrlPattern))
        {
            var nested = WebMediaUrlParser.FindNestedPageUri(
                html,
                request.NestedUrlPattern,
                effectivePageUri);
            if (nested is not null)
            {
                using var nestedMessage = new HttpRequestMessage(
                    HttpMethod.Get,
                    nested);
                HeaderNormalizer.AddToRequest(nestedMessage, request.Headers, request.Cookies);
                diagnostics.Record(
                    "http",
                    "request",
                    request.SourceId,
                    nested,
                    new Dictionary<string, object?>
                    {
                        ["method"] = "GET",
                        ["phase"] = "nested",
                    });
                var nestedStarted =
                    System.Diagnostics.Stopwatch.GetTimestamp();
                using var nestedResponse = await httpClient.SendAsync(
                    nestedMessage,
                    cancellationToken);
                diagnostics.Record(
                    "http",
                    "response",
                    request.SourceId,
                    nested,
                    new Dictionary<string, object?>
                    {
                        ["phase"] = "nested",
                        ["status"] = (int)nestedResponse.StatusCode,
                        ["contentType"] =
                            nestedResponse.Content.Headers.ContentType?.MediaType,
                        ["elapsedMs"] = System.Diagnostics.Stopwatch
                            .GetElapsedTime(nestedStarted)
                            .TotalMilliseconds,
                    });
                nestedResponse.EnsureSuccessStatusCode();
                var nestedHtml = await nestedResponse.Content.ReadAsStringAsync(
                    cancellationToken);
                diagnostics.Record(
                    "http",
                    "response-body",
                    request.SourceId,
                    nested,
                    new Dictionary<string, object?>
                    {
                        ["phase"] = "nested",
                        ["characters"] = nestedHtml.Length,
                    },
                    nestedHtml);
                var effectiveNestedUri = nestedResponse.RequestMessage?.RequestUri ?? nested;
                resolved = WebMediaUrlParser.FindMedia(nestedHtml, request, effectiveNestedUri);
                if (resolved is not null)
                {
                    return new WebResolvedMedia(
                        resolved,
                        HeaderNormalizer.BuildMediaHeaders(request, effectiveNestedUri));
                }
            }
        }

        return null;
    }
}
