using AniMeido.Plugin.Player.Views;
using AniMeido.Plugin.Player.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace AniMeido.Plugin.Player.Sources.Web;

internal sealed class WebMediaResolver : IDisposable
{
    private static readonly TimeSpan InteractionDetectionGracePeriod =
        TimeSpan.FromSeconds(2);

    private const string MediaCaptureScript =
        """
        (() => {
            if (window.__animeidoCaptureInstalled) return;
            window.__animeidoCaptureInstalled = true;
            window.__animeidoMediaCandidates = [];
            const push = (value, confirmed) => {
                if (typeof value !== "string" || !value) return;
                const candidate = {
                    url: value,
                    confirmed: !!confirmed
                };
                window.__animeidoMediaCandidates.push(candidate);
                try {
                    window.chrome?.webview?.postMessage(candidate);
                } catch {}
            };
            const inspectText = (text, responseUrl) => {
                if (typeof text !== "string") return;
                if (text.trimStart().startsWith("#EXTM3U")) {
                    push(responseUrl, true);
                }
                const matches = text.match(
                    /https?:\/\/[^\s"'<>\\]+?(?:\.m3u8|\.mp4)(?:\?[^\s"'<>\\]*)?/gi);
                if (matches) matches.forEach(url => push(url, false));
            };
            const scanDom = root => {
                if (!root || !root.querySelectorAll) return;
                root.querySelectorAll("video, source").forEach(node => {
                    push(node.currentSrc || node.src || node.getAttribute("src"), false);
                });
            };
            const startObserver = () => {
                scanDom(document);
                if (!document.documentElement) return;
                new MutationObserver(records => {
                    for (const record of records) {
                        if (record.type === "attributes") {
                            const node = record.target;
                            push(node.currentSrc || node.src || node.getAttribute?.("src"), false);
                        }
                        record.addedNodes.forEach(node => {
                            if (node.nodeType === Node.ELEMENT_NODE) {
                                if (node.matches?.("video, source")) {
                                    push(
                                        node.currentSrc || node.src || node.getAttribute("src"),
                                        false);
                                }
                                scanDom(node);
                            }
                        });
                    }
                }).observe(document.documentElement, {
                    subtree: true,
                    childList: true,
                    attributes: true,
                    attributeFilter: ["src"]
                });
            };
            if (document.readyState === "loading") {
                document.addEventListener("DOMContentLoaded", startObserver, { once: true });
            } else {
                startObserver();
            }
            const originalFetch = window.fetch;
            if (originalFetch) {
                window.fetch = async (...args) => {
                    const response = await originalFetch.apply(window, args);
                    try {
                        const clone = response.clone();
                        const type = clone.headers.get("content-type") || "";
                        if (/mpegurl|text\/plain|octet-stream/i.test(type)
                            || /\.m3u8(?:\?|$)/i.test(clone.url)) {
                            clone.text()
                                .then(text => inspectText(text, clone.url))
                                .catch(() => {});
                        }
                    } catch {}
                    return response;
                };
            }
            const originalOpen = XMLHttpRequest.prototype.open;
            const originalSend = XMLHttpRequest.prototype.send;
            XMLHttpRequest.prototype.open = function(method, url, ...rest) {
                this.__animeidoUrl = String(url);
                return originalOpen.call(this, method, url, ...rest);
            };
            XMLHttpRequest.prototype.send = function(...args) {
                this.addEventListener("loadend", () => {
                    try {
                        if (!this.responseType || this.responseType === "text") {
                            inspectText(
                                this.responseText,
                                this.responseURL || this.__animeidoUrl);
                        }
                    } catch {}
                }, { once: true });
                return originalSend.apply(this, args);
            };
        })();
        """;

    private readonly HttpClient _httpClient;
    private readonly PlayerRuntimeSettingsStore _runtimeSettings;
    private readonly HostWebSessionManager _sessionManager;
    private readonly PlaybackDiagnosticRecorder _diagnostics;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte>
        _forcedVerificationHosts = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherQueue? _dispatcher;
    private nint _hostWindowHandle;
    private CoreWebView2Environment? _environment;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _coreWebView;
    private SourceVerificationWindow? _verificationWindow;
    private CancellationTokenSource? _idleCleanupCancellation;
    private long _generation;
    private bool _disposed;

    public WebMediaResolver(
        HttpClient httpClient,
        PlayerRuntimeSettingsStore runtimeSettings,
        HostWebSessionManager sessionManager,
        PlaybackDiagnosticRecorder diagnostics)
    {
        _httpClient = httpClient;
        _runtimeSettings = runtimeSettings;
        _sessionManager = sessionManager;
        _diagnostics = diagnostics;
    }

    public void AttachUiThread(
        DispatcherQueue dispatcher,
        nint hostWindowHandle)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (hostWindowHandle == nint.Zero)
        {
            throw new ArgumentException(
                "播放器窗口句柄不能为空。",
                nameof(hostWindowHandle));
        }

        if (_hostWindowHandle != nint.Zero
            && _hostWindowHandle != hostWindowHandle)
        {
            CloseBrowserWindow();
        }

        _dispatcher = dispatcher;
        _hostWindowHandle = hostWindowHandle;
    }

    public async Task<WebResolvedMedia> ResolveAsync(
        WebResolutionRequest request,
        CancellationToken cancellationToken)
    {
        _idleCleanupCancellation?.Cancel();
        _idleCleanupCancellation?.Dispose();
        _idleCleanupCancellation = null;
        await _gate.WaitAsync(cancellationToken);
        _diagnostics.Record(
            "resolver",
            "resolve-started",
            request.SourceId,
            request.PageUri,
            new Dictionary<string, object?>
            {
                ["nested"] = request.EnableNestedUrl,
                ["scanDom"] = request.ScanDomMediaUrls,
                ["scanInlineScript"] = request.ScanInlineScriptUrls,
                ["legacy"] = request.UseLegacyParser,
                ["hasActionScript"] =
                    !string.IsNullOrWhiteSpace(request.ActionScript),
                ["headerNames"] = string.Join(
                    ",",
                    request.Headers.Keys.Order(StringComparer.OrdinalIgnoreCase)),
                ["hasCookies"] = !string.IsNullOrWhiteSpace(request.Cookies),
            });
        try
        {
            var normalizedHost = HostWebSessionManager.NormalizeHost(
                request.PageUri.Host);
            var hostSession = (await _sessionManager.ListAsync(
                    cancellationToken))
                .FirstOrDefault(item => string.Equals(
                    item.Host,
                    normalizedHost,
                    StringComparison.OrdinalIgnoreCase));
            var sessionHeaders = string.IsNullOrWhiteSpace(
                hostSession?.UserAgent)
                    ? null
                    : new Dictionary<string, string>
                    {
                        ["User-Agent"] = hostSession.UserAgent,
                    };
            request = request with
            {
                Headers = HeaderNormalizer.Merge(
                    sessionHeaders,
                    request.Headers),
            };
            if (string.IsNullOrWhiteSpace(request.Cookies))
            {
                var profileCookies = await RunOnUiAsync(
                    () => ReadCookiesOnUiAsync(
                        request.PageUri,
                        cancellationToken),
                    cancellationToken);
                request = request with { Cookies = profileCookies };
            }
            var timeout = await _runtimeSettings.GetEffectiveTimeoutAsync(
                request.SourceId,
                request.SourceDeclaredTimeout,
                cancellationToken);
            var forceVerification =
                _forcedVerificationHosts.TryRemove(normalizedHost, out _);
            var initialAttempt = forceVerification
                ? new WebViewResolutionAttempt(
                    null,
                    WebPageInteractionKind.HumanVerification)
                : await RunBackgroundAttemptAsync(
                    request,
                    timeout,
                    cancellationToken);
            if (initialAttempt.Media is not null)
            {
                var validated = await ValidateMediaCandidateAsync(
                    initialAttempt.Media,
                    request.PageUri,
                    request.SourceId,
                    cancellationToken);
                _diagnostics.Record(
                    "resolver",
                    "resolve-succeeded",
                    request.SourceId,
                    validated.Uri,
                    new Dictionary<string, object?>
                    {
                        ["path"] = "background",
                        ["headerNames"] = string.Join(
                            ",",
                            validated.Headers.Keys.Order(
                                StringComparer.OrdinalIgnoreCase)),
                    });
                return validated;
            }

            if (initialAttempt.Interaction == WebPageInteractionKind.None)
            {
                throw new SourceResolutionException(
                    SourceResolutionFailureKind.RuleMismatch,
                    "未能从后台页面解析到媒体地址。"
                    + "可能是播放源规则已失效，或页面使用了尚未支持的加载方式。",
                    request.PageUri);
            }

            WebResolvedMedia? verifiedMedia = null;
            async Task<bool> VerifyAsync(CancellationToken verificationToken)
            {
                var refreshedCookies = await RunOnUiAsync(
                    () => ReadCookiesOnUiAsync(
                        request.PageUri,
                        verificationToken),
                    verificationToken);
                var retryRequest = request with
                {
                    Cookies = refreshedCookies,
                };
                var retry = await RunBackgroundAttemptAsync(
                    retryRequest,
                    timeout,
                    verificationToken);
                verifiedMedia = retry.Media;
                return verifiedMedia is not null;
            }

            _diagnostics.Record(
                "verification",
                "opened",
                request.SourceId,
                request.PageUri,
                new Dictionary<string, object?>
                {
                    ["interaction"] =
                        initialAttempt.Interaction.ToString(),
                });
            var verified = await RunOnUiAsync(
                () => ShowVerificationWindowAsync(
                    request.PageUri,
                    request.Headers,
                    request.Cookies,
                    VerifyAsync,
                    cancellationToken),
                cancellationToken);
            _diagnostics.Record(
                "verification",
                verified ? "completed" : "cancelled",
                request.SourceId,
                request.PageUri);
            if (!verified || verifiedMedia is null)
            {
                throw new OperationCanceledException(
                    "已取消源站登录或人机验证。",
                    cancellationToken);
            }

            var userAgent = verifiedMedia.Headers.GetValueOrDefault(
                "User-Agent");
            await _sessionManager.RecordVerifiedAsync(
                request.PageUri.Host,
                request.SourceId,
                userAgent,
                cancellationToken);
            var validatedAfterVerification = await ValidateMediaCandidateAsync(
                verifiedMedia,
                request.PageUri,
                request.SourceId,
                cancellationToken);
            _diagnostics.Record(
                "resolver",
                "resolve-succeeded",
                request.SourceId,
                validatedAfterVerification.Uri,
                new Dictionary<string, object?>
                {
                    ["path"] = "verified",
                    ["headerNames"] = string.Join(
                        ",",
                        validatedAfterVerification.Headers.Keys.Order(
                            StringComparer.OrdinalIgnoreCase)),
                });
            return validatedAfterVerification;
        }
#pragma warning disable CA1031 // Diagnostics must observe every resolver failure.
        catch (Exception ex)
        {
            _diagnostics.Record(
                "resolver",
                "resolve-failed",
                request.SourceId,
                request.PageUri,
                new Dictionary<string, object?>
                {
                    ["exception"] = ex.GetType().Name,
                    ["message"] = ex.Message,
                    ["failureKind"] =
                        (ex as SourceResolutionException)?.Kind.ToString(),
                    ["cancelled"] = cancellationToken.IsCancellationRequested,
                });
            throw;
        }
#pragma warning restore CA1031
        finally
        {
            _gate.Release();
            ScheduleIdleCleanup();
        }
    }

    private void ScheduleIdleCleanup()
    {
        if (_disposed)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _idleCleanupCancellation = cancellation;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), cancellation.Token);
                if (_dispatcher is not null)
                {
                    _ = _dispatcher.TryEnqueue(CloseHiddenController);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void CloseHiddenController()
    {
        _coreWebView = null;
        var controller = _controller;
        _controller = null;
        if (controller is not null)
        {
            TryRunWebViewCleanup(controller.Close);
        }
    }

    private async Task<string?> ReadCookiesOnUiAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        await EnsureWebViewAsync(cancellationToken);
        var cookies = await _coreWebView!.CookieManager.GetCookiesAsync(
            uri.AbsoluteUri);
        return cookies.Count == 0
            ? null
            : string.Join(
                "; ",
                cookies.Select(cookie => $"{cookie.Name}={cookie.Value}"));
    }

    private async Task<WebResolvedMedia> ValidateMediaCandidateAsync(
        WebResolvedMedia media,
        Uri pageUri,
        string sourceId,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, media.Uri);
        AddHeaders(message, media.Headers, cookies: null);
        var useRangeProbe = ShouldUseRangeProbe(media.Uri);
        if (useRangeProbe)
        {
            message.Headers.Range =
                new System.Net.Http.Headers.RangeHeaderValue(0, 1023);
        }
        else
        {
            message.Headers.Accept.ParseAdd(
                "application/vnd.apple.mpegurl, application/x-mpegURL, */*");
        }

        _diagnostics.Record(
            "media-probe",
            "request",
            sourceId,
            media.Uri,
            data: new Dictionary<string, object?>
            {
                ["method"] = "GET",
                ["range"] = useRangeProbe ? "bytes=0-1023" : null,
                ["headerNames"] = string.Join(
                    ",",
                    media.Headers.Keys.Order(StringComparer.OrdinalIgnoreCase)),
            });
        var probeStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        using var response = await _httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        _diagnostics.Record(
            "media-probe",
            "response",
            sourceId,
            media.Uri,
            data: new Dictionary<string, object?>
            {
                ["status"] = (int)response.StatusCode,
                ["contentType"] =
                    response.Content.Headers.ContentType?.MediaType,
                ["contentLength"] =
                    response.Content.Headers.ContentLength,
                ["acceptRanges"] = string.Join(
                    ",",
                    response.Headers.AcceptRanges),
                ["elapsedMs"] = System.Diagnostics.Stopwatch
                    .GetElapsedTime(probeStarted)
                    .TotalMilliseconds,
            });
        if (response.StatusCode is HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.NotFound)
        {
            var rejectionBody = await ReadResponsePrefixAsync(
                response.Content,
                4096,
                cancellationToken);
            _diagnostics.Record(
                "media-probe",
                "rejected-body",
                sourceId,
                media.Uri,
                data: new Dictionary<string, object?>
                {
                    ["status"] = (int)response.StatusCode,
                    ["bytes"] = System.Text.Encoding.UTF8.GetByteCount(
                        rejectionBody),
                },
                rejectionBody);
            throw new SourceResolutionException(
                SourceResolutionFailureKind.MediaRejected,
                DescribeMediaRejection(
                    response.StatusCode,
                    rejectionBody),
                pageUri);
        }

        response.EnsureSuccessStatusCode();
        var contentType =
            response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains(
                "mpegurl",
                StringComparison.OrdinalIgnoreCase)
            || contentType.Contains(
                "octet-stream",
                StringComparison.OrdinalIgnoreCase))
        {
            return media;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        var buffer = new byte[1024];
        var length = await stream.ReadAsync(buffer, cancellationToken);
        var prefix = System.Text.Encoding.UTF8.GetString(buffer, 0, length);
        _diagnostics.Record(
            "media-probe",
            "body-prefix",
            sourceId,
            media.Uri,
            data: new Dictionary<string, object?>
            {
                ["bytes"] = length,
            },
            prefix);
        if (prefix.TrimStart().StartsWith(
                "#EXTM3U",
                StringComparison.OrdinalIgnoreCase)
            || media.Uri.AbsolutePath.EndsWith(
                ".mp4",
                StringComparison.OrdinalIgnoreCase))
        {
            return media;
        }

        throw new SourceResolutionException(
            SourceResolutionFailureKind.MediaRejected,
            "媒体候选返回了非视频内容，已拒绝交给播放器。",
            pageUri);
    }

    internal static string DescribeMediaRejection(
        HttpStatusCode status,
        string responsePrefix)
    {
        if (responsePrefix.Contains(
                "region has been denied",
                StringComparison.OrdinalIgnoreCase))
        {
            return "媒体 CDN 拒绝当前网络地区。"
                + "请检查 VPN、TUN 或代理分流，并尝试让媒体域名直连。";
        }

        return status switch
        {
            HttpStatusCode.NotFound =>
                "媒体地址已失效（HTTP 404），请切换线路或刷新播放源。",
            HttpStatusCode.Unauthorized =>
                "媒体请求未获授权（HTTP 401），可能需要重新登录源站。",
            _ => $"媒体候选被源站拒绝（HTTP {(int)status}）。",
        };
    }

    private static async Task<string> ReadResponsePrefixAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(
            cancellationToken);
        var buffer = new byte[maximumBytes];
        var length = await stream.ReadAsync(buffer, cancellationToken);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, length);
    }

    public void RequireVerificationOnNextResolve(Uri pageUri)
    {
        ArgumentNullException.ThrowIfNull(pageUri);
        _forcedVerificationHosts[
            HostWebSessionManager.NormalizeHost(pageUri.Host)] = 0;
    }

    private async Task<WebViewResolutionAttempt> RunBackgroundAttemptAsync(
        WebResolutionRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        try
        {
            WebResolvedMedia? direct = null;
            try
            {
                direct = await TryResolveFromHttpAsync(
                    request,
                    timeoutCancellation.Token);
            }
            catch (HttpRequestException)
            {
            }
            catch (RegexMatchTimeoutException)
            {
            }
            catch (SourceResolutionException ex)
                when (ex.Kind is SourceResolutionFailureKind.Authentication
                    or SourceResolutionFailureKind.AccessDenied)
            {
                // A browser profile may already have the session that the
                // direct HttpClient request lacks. Let WebView2 classify it.
            }

            if (direct is not null)
            {
                return new WebViewResolutionAttempt(
                    direct,
                    WebPageInteractionKind.None);
            }

            return await RunOnUiAsync(
                () => TryResolveWithWebViewOnUiAsync(
                    request,
                    timeoutCancellation.Token),
                timeoutCancellation.Token);
        }
        catch (OperationCanceledException ex)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new SourceResolutionException(
                SourceResolutionFailureKind.Timeout,
                $"播放源解析超过 {timeout.TotalSeconds:0} 秒。",
                request.PageUri,
                ex);
        }
    }

    public async Task<string> LoadPageHtmlAsync(
        Uri uri,
        bool interactive,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await RunOnUiAsync(
                () => LoadPageHtmlOnUiAsync(
                    uri,
                    interactive,
                    cancellationToken),
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<IReadOnlyList<HostWebSessionMetadata>> ListSessionsAsync(
        CancellationToken cancellationToken)
        => _sessionManager.ListAsync(cancellationToken);

    public async Task ClearSessionAsync(
        string? host,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await RunOnUiAsync(async () =>
            {
                await EnsureWebViewAsync(cancellationToken);
                var cookieManager = _coreWebView!.CookieManager;
                if (host is null)
                {
                    cookieManager.DeleteAllCookies();
                }
                else
                {
                    var normalizedHost =
                        HostWebSessionManager.NormalizeHost(host);
                    var hosts = new[]
                    {
                        normalizedHost,
                        $"www.{normalizedHost}",
                    };
                    foreach (var scheme in new[] { "https", "http" })
                    {
                        foreach (var candidateHost in hosts)
                        {
                            var cookies =
                                await cookieManager.GetCookiesAsync(
                                    $"{scheme}://{candidateHost}/");
                            foreach (var cookie in cookies)
                            {
                                cookieManager.DeleteCookie(cookie);
                            }
                        }
                    }
                }

                return true;
            }, cancellationToken);
            await _sessionManager.RemoveMetadataAsync(
                host,
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<WebResolvedMedia?> TryResolveFromHttpAsync(
        WebResolutionRequest request,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            request.PageUri);
        AddHeaders(message, request.Headers, request.Cookies);
        _diagnostics.Record(
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
        using var response = await _httpClient.SendAsync(
            message,
            cancellationToken);
        _diagnostics.Record(
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
        _diagnostics.Record(
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
        var resolved = FindMedia(html, request, request.PageUri);
        if (resolved is not null)
        {
            return new WebResolvedMedia(
                resolved,
                BuildMediaHeaders(request, request.PageUri));
        }

        if (request.EnableNestedUrl
            && !string.IsNullOrWhiteSpace(request.NestedUrlPattern))
        {
            var nested = FindNestedPageUri(
                html,
                request.NestedUrlPattern,
                request.PageUri);
            if (nested is not null)
            {
                using var nestedMessage = new HttpRequestMessage(
                    HttpMethod.Get,
                    nested);
                AddHeaders(nestedMessage, request.Headers, request.Cookies);
                _diagnostics.Record(
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
                using var nestedResponse = await _httpClient.SendAsync(
                    nestedMessage,
                    cancellationToken);
                _diagnostics.Record(
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
                _diagnostics.Record(
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
                resolved = FindMedia(nestedHtml, request, nested);
                if (resolved is not null)
                {
                    return new WebResolvedMedia(
                        resolved,
                        BuildMediaHeaders(request, nested));
                }
            }
        }

        return null;
    }

    private async Task<WebViewResolutionAttempt> TryResolveWithWebViewOnUiAsync(
        WebResolutionRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureWebViewAsync(cancellationToken);
        var generation = Interlocked.Increment(ref _generation);
        var completion = new TaskCompletionSource<Uri>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var interactionCompletion =
            new TaskCompletionSource<WebPageInteractionKind>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyDictionary<string, string>? capturedMediaHeaders = null;
        var nestedNavigated = false;
        var actionExecuted = false;

        void OnResponse(
            CoreWebView2 sender,
            CoreWebView2WebResourceResponseReceivedEventArgs args)
        {
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }

            try
            {
                var responseHeaders = args.Response.Headers;
                var responseUri = Uri.TryCreate(
                    args.Request.Uri,
                    UriKind.Absolute,
                    out var parsedResponseUri)
                        ? parsedResponseUri
                        : null;
                _diagnostics.Record(
                    "webview",
                    "response",
                    request.SourceId,
                    responseUri,
                    new Dictionary<string, object?>
                    {
                        ["status"] = args.Response.StatusCode,
                        ["contentType"] =
                            responseHeaders.Contains("Content-Type")
                                ? responseHeaders.GetHeader("Content-Type")
                                : null,
                        ["hasRange"] =
                            args.Request.Headers.Contains("Range"),
                    });
                if (args.Response.StatusCode is >= 200 and < 300
                    && TryMatchObservedMediaUrl(
                        args.Request.Uri,
                        request.VideoUrlPattern,
                        request.PageUri,
                        out var mediaUri))
                {
                    capturedMediaHeaders = ReadMediaRequestHeaders(
                        args.Request.Headers);
                    completion.TrySetResult(mediaUri);
                    return;
                }

                if (responseHeaders.Contains("Content-Type")
                    && IsMediaResponse(
                        responseHeaders.GetHeader("Content-Type"),
                        args.Request.Headers.Contains("Range"))
                    && Uri.TryCreate(
                        args.Request.Uri,
                        UriKind.Absolute,
                        out mediaUri)
                    && mediaUri.Scheme is "http" or "https"
                    && !IsIgnoredMediaUrl(mediaUri))
                {
                    capturedMediaHeaders = ReadMediaRequestHeaders(
                        args.Request.Headers);
                    completion.TrySetResult(mediaUri);
                }
            }
            catch (Exception ex)
                when (ex is COMException
                    or InvalidOperationException
                    or RegexMatchTimeoutException)
            {
                // A cancelled or closing background WebView can invalidate an
                // in-flight response. Other responses may still resolve media.
            }
        }

        void OnWebMessageReceived(
            CoreWebView2 sender,
            CoreWebView2WebMessageReceivedEventArgs args)
        {
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }

            try
            {
                var candidate = JsonSerializer.Deserialize<WebCapturedUrl>(
                    args.WebMessageAsJson);
                if (candidate is null)
                {
                    return;
                }

                var messageBaseUri = Uri.TryCreate(
                    sender.Source,
                    UriKind.Absolute,
                    out var currentSourceUri)
                        ? currentSourceUri
                        : request.PageUri;
                var matched = (candidate.Confirmed
                        && TryCreateMediaUri(
                            candidate.Url,
                            messageBaseUri,
                            out var mediaUri))
                    || TryMatchObservedMediaUrl(
                        candidate.Url,
                        request.VideoUrlPattern,
                        messageBaseUri,
                        out mediaUri);
                _diagnostics.Record(
                    "webview",
                    "script-candidate",
                    request.SourceId,
                    matched ? mediaUri : null,
                    new Dictionary<string, object?>
                    {
                        ["confirmed"] = candidate.Confirmed,
                        ["matched"] = matched,
                        ["candidate"] = candidate.Url,
                    });
                if (matched)
                {
                    capturedMediaHeaders =
                        BuildDocumentRequestHeaders(sender.Source);
                    completion.TrySetResult(mediaUri);
                }
            }
            catch (Exception ex)
                when (ex is COMException
                    or JsonException
                    or InvalidOperationException
                    or RegexMatchTimeoutException)
            {
                // Ignore unrelated or malformed messages from source pages.
            }
        }

        async void OnNavigationCompleted(
            CoreWebView2 sender,
            CoreWebView2NavigationCompletedEventArgs args)
        {
            _diagnostics.Record(
                "webview",
                "navigation-completed",
                request.SourceId,
                Uri.TryCreate(sender.Source, UriKind.Absolute, out var sourceUri)
                    ? sourceUri
                    : request.PageUri,
                new Dictionary<string, object?>
                {
                    ["success"] = args.IsSuccess,
                    ["webError"] = args.WebErrorStatus.ToString(),
                    ["generation"] = generation,
                });
            if (generation != Volatile.Read(ref _generation)
                || !args.IsSuccess
                || completion.Task.IsCompleted)
            {
                return;
            }

            try
            {
                if (!actionExecuted
                    && !string.IsNullOrWhiteSpace(request.ActionScript))
                {
                    try
                    {
                        await sender.ExecuteScriptAsync(request.ActionScript);
                        actionExecuted = true;
                        _diagnostics.Record(
                            "webview",
                            "action-script-completed",
                            request.SourceId,
                            request.PageUri);
                    }
                    catch (Exception ex)
                        when (ex is COMException
                            or InvalidOperationException)
                    {
                        completion.TrySetException(
                            new SourceResolutionException(
                                SourceResolutionFailureKind.RuleMismatch,
                                "播放源 actionJs 执行失败；该源与当前兼容层不匹配。",
                                request.PageUri,
                                ex));
                        return;
                    }
                }

                if ((request.EnableNestedUrl || request.UseLegacyParser)
                    && !nestedNavigated)
                {
                    var iframeJson = await sender.ExecuteScriptAsync(
                        """
                        Array.from(document.querySelectorAll("iframe"))
                            .map(frame => frame.src)
                            .filter(Boolean)
                        """);
                    var iframeUrls =
                        JsonSerializer.Deserialize<string[]>(iframeJson) ?? [];
                    var iframeUri = iframeUrls
                        .Select(value => Uri.TryCreate(
                            request.PageUri,
                            value,
                            out var uri)
                                ? uri
                                : null)
                        .FirstOrDefault(uri =>
                            uri is not null
                            && uri.Scheme is "http" or "https");
                    if (iframeUri is not null)
                    {
                        nestedNavigated = true;
                        _diagnostics.Record(
                            "webview",
                            "nested-iframe-navigation",
                            request.SourceId,
                            iframeUri);
                        var nestedHeaders = HeaderNormalizer.Merge(
                            request.Headers,
                            new Dictionary<string, string>
                            {
                                ["Referer"] = sender.Source,
                            });
                        Navigate(
                            sender,
                            iframeUri,
                            nestedHeaders,
                            request.Cookies);
                        return;
                    }
                }

                var json = await sender.ExecuteScriptAsync(
                    "document.documentElement.outerHTML");
                var html = JsonSerializer.Deserialize<string>(json) ?? string.Empty;
                var currentDocumentUri = Uri.TryCreate(
                    sender.Source,
                    UriKind.Absolute,
                    out var documentUri)
                        ? documentUri
                        : request.PageUri;
                _diagnostics.Record(
                    "webview",
                    "document-html",
                    request.SourceId,
                    currentDocumentUri,
                    new Dictionary<string, object?>
                    {
                        ["characters"] = html.Length,
                        ["nested"] = nestedNavigated,
                    },
                    html);
                var mediaUri = FindMedia(
                    html,
                    request,
                    currentDocumentUri);
                if (mediaUri is not null)
                {
                    capturedMediaHeaders =
                        BuildDocumentRequestHeaders(sender.Source);
                    completion.TrySetResult(mediaUri);
                    return;
                }

                var interaction = await DetectInteractionAsync(sender);
                _diagnostics.Record(
                    "webview",
                    "interaction-classified",
                    request.SourceId,
                    request.PageUri,
                    new Dictionary<string, object?>
                    {
                        ["interaction"] = interaction.ToString(),
                    });
                if (interaction != WebPageInteractionKind.None)
                {
                    await Task.Delay(
                        InteractionDetectionGracePeriod,
                        cancellationToken);
                    if (!completion.Task.IsCompleted)
                    {
                        interactionCompletion.TrySetResult(interaction);
                    }
                }
            }
#pragma warning disable CA1031 // Network capture remains the primary resolver.
            catch (Exception)
            {
            }
#pragma warning restore CA1031
        }

        var webView = _coreWebView!;
        webView.WebResourceResponseReceived += OnResponse;
        webView.WebMessageReceived += OnWebMessageReceived;
        webView.NavigationCompleted += OnNavigationCompleted;
        try
        {
            Navigate(webView, request.PageUri, request.Headers, request.Cookies);
            using var pollingCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            var domPolling = PollForMediaAsync(
                webView,
                request,
                completion,
                headers => capturedMediaHeaders = headers,
                pollingCancellation.Token);
            try
            {
                var completed = await Task.WhenAny(
                        completion.Task,
                        interactionCompletion.Task)
                    .WaitAsync(cancellationToken);
                if (ReferenceEquals(completed, interactionCompletion.Task))
                {
                    return new WebViewResolutionAttempt(
                        null,
                        await interactionCompletion.Task);
                }

                var uri = await completion.Task;
                var resolvedHeaders = HeaderNormalizer.Merge(
                    await BuildWebViewHeadersAsync(request, uri),
                    capturedMediaHeaders);
                return new WebViewResolutionAttempt(
                    new WebResolvedMedia(
                        uri,
                        resolvedHeaders),
                    WebPageInteractionKind.None);
            }
            finally
            {
                pollingCancellation.Cancel();
                await domPolling;
            }
        }
        finally
        {
            TryRunWebViewCleanup(() =>
            {
                webView.WebResourceResponseReceived -= OnResponse;
                webView.WebMessageReceived -= OnWebMessageReceived;
                webView.NavigationCompleted -= OnNavigationCompleted;
                ResetWebView(webView);
            });
        }
    }

    private async Task<string> LoadPageHtmlOnUiAsync(
        Uri uri,
        bool interactive,
        CancellationToken cancellationToken)
    {
        await EnsureWebViewAsync(cancellationToken);
        if (interactive)
        {
            ResetWebView(_coreWebView!);
            var verified = await ShowVerificationWindowAsync(
                uri,
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase),
                cookies: null,
                verifyAsync: verificationToken =>
                    VerifyPageAccessOnUiAsync(uri, verificationToken),
                cancellationToken: cancellationToken);
            if (!verified)
            {
                throw new InvalidOperationException(
                    "已取消源站登录或人机验证。");
            }
        }

        var navigation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnNavigationCompleted(
            CoreWebView2 sender,
            CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.IsSuccess)
            {
                navigation.TrySetResult();
            }
            else
            {
                navigation.TrySetException(new InvalidOperationException(
                    $"WebView2 页面加载失败：{args.WebErrorStatus}"));
            }
        }

        var webView = _coreWebView!;
        webView.NavigationCompleted += OnNavigationCompleted;
        try
        {
            Navigate(
                webView,
                uri,
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase),
                cookies: null);
            await navigation.Task.WaitAsync(cancellationToken);

            var json = await webView.ExecuteScriptAsync(
                "document.documentElement.outerHTML");
            return JsonSerializer.Deserialize<string>(json) ?? string.Empty;
        }
        finally
        {
            TryRunWebViewCleanup(() =>
            {
                webView.NavigationCompleted -= OnNavigationCompleted;
                ResetWebView(webView);
            });
        }
    }

    private async Task<bool> VerifyPageAccessOnUiAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        await EnsureWebViewAsync(cancellationToken);
        var navigation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnNavigationCompleted(
            CoreWebView2 sender,
            CoreWebView2NavigationCompletedEventArgs args)
            => navigation.TrySetResult();

        var webView = _coreWebView!;
        webView.NavigationCompleted += OnNavigationCompleted;
        try
        {
            webView.Navigate(uri.AbsoluteUri);
            await navigation.Task.WaitAsync(
                TimeSpan.FromSeconds(
                    PlayerRuntimeSettingsStore.DefaultTimeoutSeconds),
                cancellationToken);
            return await DetectInteractionAsync(webView)
                == WebPageInteractionKind.None;
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            TryRunWebViewCleanup(() =>
            {
                webView.NavigationCompleted -= OnNavigationCompleted;
                ResetWebView(webView);
            });
        }
    }

    private async Task<T> RunOnUiAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        if (_dispatcher is null)
        {
            throw new InvalidOperationException(
                "网页解析器尚未绑定播放器 UI 线程。");
        }

        if (_dispatcher.HasThreadAccess)
        {
            return await operation();
        }

        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(async () =>
        {
            try
            {
                completion.SetResult(await operation());
            }
#pragma warning disable CA1031 // Preserve arbitrary operation failures for the awaiting caller.
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
#pragma warning restore CA1031
        }))
        {
            throw new InvalidOperationException("无法调度 WebView2 操作。");
        }

        return await completion.Task.WaitAsync(cancellationToken);
    }

    private async Task EnsureWebViewAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_coreWebView is not null)
        {
            return;
        }

        if (_dispatcher is null)
        {
            throw new InvalidOperationException(
                "网页解析器尚未绑定播放器 UI 线程。");
        }

        if (_hostWindowHandle == nint.Zero)
        {
            throw new InvalidOperationException(
                "网页解析器尚未绑定播放器窗口。");
        }

        if (!_dispatcher.HasThreadAccess)
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    await CreateWebViewAsync();
                    completion.SetResult();
                }
#pragma warning disable CA1031 // Preserve WebView2 initialization failures for the awaiting caller.
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
#pragma warning restore CA1031
            }))
            {
                throw new InvalidOperationException("无法调度 WebView2 初始化。");
            }

            await completion.Task.WaitAsync(cancellationToken);
            return;
        }

        await CreateWebViewAsync();
    }

    private async Task CreateWebViewAsync()
    {
        if (_coreWebView is not null)
        {
            return;
        }

        _environment ??= await _sessionManager.GetEnvironmentAsync();
        var parentWindow =
            CoreWebView2ControllerWindowReference.CreateFromWindowHandle(
                unchecked((ulong)_hostWindowHandle.ToInt64()));
        var controller = await _environment.CreateCoreWebView2ControllerAsync(
            parentWindow);
        controller.Bounds = new Windows.Foundation.Rect(0, 0, 1, 1);
        controller.IsVisible = false;
        _controller = controller;
        _coreWebView = controller.CoreWebView2;
        await _coreWebView.AddScriptToExecuteOnDocumentCreatedAsync(
            MediaCaptureScript);
        _coreWebView.AddWebResourceRequestedFilter(
            "*",
            CoreWebView2WebResourceContext.All);
    }

    private void Navigate(
        CoreWebView2 webView,
        Uri uri,
        IReadOnlyDictionary<string, string> headers,
        string? cookies)
    {
        if (headers.TryGetValue("User-Agent", out var userAgent)
            && !string.IsNullOrWhiteSpace(userAgent))
        {
            webView.Settings.UserAgent = userAgent;
        }

        var allHeaders = HeaderNormalizer.Merge(headers);
        if (!string.IsNullOrWhiteSpace(cookies))
        {
            allHeaders["Cookie"] = cookies;
        }

        var headerText = string.Join(
            "\r\n",
            allHeaders.Select(header => $"{header.Key}: {header.Value}"));
        var request = _environment!.CreateWebResourceRequest(
            uri.AbsoluteUri,
            "GET",
            null,
            headerText);
        webView.NavigateWithWebResourceRequest(request);
    }

    private async Task PollForMediaAsync(
        CoreWebView2 webView,
        WebResolutionRequest request,
        TaskCompletionSource<Uri> completion,
        Action<IReadOnlyDictionary<string, string>> captureHeaders,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (!completion.Task.IsCompleted
            && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var json = await webView.ExecuteScriptAsync(
                    """
                    (() => {
                        const candidates =
                            Array.isArray(window.__animeidoMediaCandidates)
                                ? window.__animeidoMediaCandidates.splice(0)
                                : [];
                        document.querySelectorAll("video, source").forEach(node => {
                            const value = node.currentSrc || node.src
                                || node.getAttribute("src");
                            if (value) candidates.push({
                                url: value,
                                confirmed: false
                            });
                        });
                        performance.getEntriesByType("resource").forEach(entry => {
                            if (entry.name) candidates.push({
                                url: entry.name,
                                confirmed: false
                            });
                        });
                        return candidates;
                    })()
                    """);
                var candidates =
                    JsonSerializer.Deserialize<WebCapturedUrl[]>(json) ?? [];
                foreach (var candidate in candidates)
                {
                    if (!seen.Add(candidate.Url))
                    {
                        continue;
                    }

                    var matched = (candidate.Confirmed
                            && TryCreateMediaUri(
                                candidate.Url,
                                request.PageUri,
                                out var confirmedUri))
                        || TryMatchObservedMediaUrl(
                            candidate.Url,
                            request.VideoUrlPattern,
                            request.PageUri,
                            out confirmedUri);
                    _diagnostics.Record(
                        "webview",
                        "dom-candidate",
                        request.SourceId,
                        matched ? confirmedUri : null,
                        new Dictionary<string, object?>
                        {
                            ["confirmed"] = candidate.Confirmed,
                            ["matched"] = matched,
                            ["candidate"] = candidate.Url,
                        });
                    if (matched)
                    {
                        captureHeaders(
                            BuildDocumentRequestHeaders(webView.Source));
                        completion.TrySetResult(confirmedUri);
                        return;
                    }
                }
            }
#pragma warning disable CA1031 // Network interception remains the primary path.
            catch (Exception)
            {
            }
#pragma warning restore CA1031

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static void ResetWebView(CoreWebView2 webView)
    {
        webView.Stop();
        webView.Navigate("about:blank");
    }

    private static void TryRunWebViewCleanup(Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception ex)
            when (ex is COMException or InvalidOperationException)
        {
            // The parent player window can close while a resolution is being
            // cancelled. Its child WebView2 controller is already unusable.
        }
    }

    private async Task<IReadOnlyDictionary<string, string>>
        BuildWebViewHeadersAsync(
            WebResolutionRequest request,
            Uri mediaUri)
    {
        var headers = BuildMediaHeaders(request, request.PageUri);
        var pageCookies = await _coreWebView!.CookieManager.GetCookiesAsync(
            request.PageUri.AbsoluteUri);
        IEnumerable<CoreWebView2Cookie> mediaCookies =
            request.PageUri.Host.Equals(
                mediaUri.Host,
                StringComparison.OrdinalIgnoreCase)
                    ? Array.Empty<CoreWebView2Cookie>()
                    : await _coreWebView.CookieManager.GetCookiesAsync(
                        mediaUri.AbsoluteUri);
        var cookies = pageCookies
            .Concat(mediaCookies)
            .GroupBy(cookie => cookie.Name, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToArray();
        if (cookies.Length > 0)
        {
            headers["Cookie"] = string.Join(
                "; ",
                cookies.Select(cookie => $"{cookie.Name}={cookie.Value}"));
        }

        if (!headers.ContainsKey("User-Agent")
            && !string.IsNullOrWhiteSpace(
                _coreWebView.Settings.UserAgent))
        {
            headers["User-Agent"] =
                _coreWebView.Settings.UserAgent;
        }

        return headers;
    }

    private static Dictionary<string, string> BuildMediaHeaders(
        WebResolutionRequest request,
        Uri referer)
    {
        var headers = HeaderNormalizer.Merge(request.Headers);
        if (!headers.ContainsKey("Referer"))
        {
            headers["Referer"] = referer.AbsoluteUri;
        }

        if (!headers.ContainsKey("Origin")
            && Uri.TryCreate(
                headers.GetValueOrDefault("Referer"),
                UriKind.Absolute,
                out var effectiveReferer))
        {
            headers["Origin"] =
                effectiveReferer.GetLeftPart(UriPartial.Authority);
        }

        if (!string.IsNullOrWhiteSpace(request.Cookies))
        {
            headers["Cookie"] = request.Cookies;
        }

        return headers;
    }

    internal static IReadOnlyDictionary<string, string>
        BuildDocumentRequestHeaders(string source)
    {
        var headers = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        if (!Uri.TryCreate(source, UriKind.Absolute, out var documentUri)
            || documentUri.Scheme is not ("http" or "https"))
        {
            return headers;
        }

        headers["Referer"] = documentUri.AbsoluteUri;
        headers["Origin"] = documentUri.GetLeftPart(UriPartial.Authority);
        return headers;
    }

    private static void AddHeaders(
        HttpRequestMessage message,
        IReadOnlyDictionary<string, string> headers,
        string? cookies)
    {
        foreach (var header in headers)
        {
            message.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (!string.IsNullOrWhiteSpace(cookies))
        {
            message.Headers.TryAddWithoutValidation("Cookie", cookies);
        }
    }

    private static Uri? FindMedia(
        string content,
        WebResolutionRequest request,
        Uri baseUri)
    {
        var matched = FindFirstUrl(
            content,
            request.VideoUrlPattern,
            baseUri);
        return matched is not null
            && TryExtractEmbeddedMediaUri(
                matched,
                request.VideoUrlPattern,
                baseUri,
                out var embedded)
                    ? embedded
                    : matched;
    }

    internal static Uri? FindNestedPageUri(
        string content,
        string pattern,
        Uri baseUri)
    {
        var iframePattern = new Regex(
            """<iframe\b[^>]*\bsrc\s*=\s*["'](?<v>[^"']+)["']""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2));
        foreach (Match match in iframePattern.Matches(content))
        {
            var value = WebUtility.HtmlDecode(match.Groups["v"].Value);
            if (Uri.TryCreate(baseUri, value, out var iframeUri)
                && iframeUri.Scheme is "http" or "https"
                && !IsIgnoredMediaUrl(iframeUri))
            {
                return iframeUri;
            }
        }

        return FindFirstUrl(content, pattern, baseUri);
    }

    private static Uri? FindFirstUrl(
        string content,
        string pattern,
        Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        var regex = new Regex(
            pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2));
        foreach (Match match in regex.Matches(content))
        {
            var value = match.Groups["v"].Success
                ? match.Groups["v"].Value
                : match.Value;
            value = value
                .Replace("\\/", "/", StringComparison.Ordinal)
                .Replace("&amp;", "&", StringComparison.Ordinal);
            if (value.StartsWith("url=", StringComparison.OrdinalIgnoreCase))
            {
                value = value[4..];
            }

            value = Uri.UnescapeDataString(value);
            if (Uri.TryCreate(value, UriKind.Absolute, out var absolute)
                && absolute.Scheme is "http" or "https"
                && !IsIgnoredMediaUrl(absolute))
            {
                return absolute;
            }

            if (Uri.TryCreate(baseUri, value, out var relative)
                && relative.Scheme is "http" or "https"
                && !IsIgnoredMediaUrl(relative))
            {
                return relative;
            }
        }

        return null;
    }

    private static bool TryMatchUrl(
        string value,
        string pattern,
        Uri baseUri,
        out Uri mediaUri)
    {
        var matched = FindFirstUrl(value, pattern, baseUri);
        if (matched is not null
            && TryExtractEmbeddedMediaUri(
                matched,
                pattern,
                baseUri,
                out var embedded))
        {
            mediaUri = embedded;
            return true;
        }

        mediaUri = matched!;
        return matched is not null;
    }

    internal static bool TryMatchObservedMediaUrl(
        string value,
        string pattern,
        Uri baseUri,
        out Uri mediaUri)
    {
        if (!TryMatchUrl(value, pattern, baseUri, out mediaUri))
        {
            return false;
        }

        if (!Uri.TryCreate(baseUri, value, out var observedUri))
        {
            return true;
        }

        return observedUri.Scheme.Equals(
                mediaUri.Scheme,
                StringComparison.OrdinalIgnoreCase)
            && observedUri.Host.Equals(
                mediaUri.Host,
                StringComparison.OrdinalIgnoreCase)
            && observedUri.Port == mediaUri.Port
            && observedUri.AbsolutePath.Equals(
                mediaUri.AbsolutePath,
                StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ShouldUseRangeProbe(Uri mediaUri)
    {
        ArgumentNullException.ThrowIfNull(mediaUri);
        return !mediaUri.AbsolutePath.EndsWith(
            ".m3u8",
            StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string> ReadMediaRequestHeaders(
        CoreWebView2HttpRequestHeaders requestHeaders)
    {
        var headers = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "User-Agent", "Referer", "Origin", "Cookie" })
        {
            if (requestHeaders.Contains(name))
            {
                headers[name] = requestHeaders.GetHeader(name);
            }
        }

        return headers;
    }

    internal static bool TryExtractEmbeddedMediaUri(
        Uri wrapperUri,
        string pattern,
        Uri baseUri,
        out Uri mediaUri)
    {
        mediaUri = null!;
        if (string.IsNullOrWhiteSpace(wrapperUri.Query))
        {
            return false;
        }

        foreach (var pair in wrapperUri.Query.TrimStart('?').Split('&'))
        {
            var separator = pair.IndexOf('=');
            if (separator < 0 || separator == pair.Length - 1)
            {
                continue;
            }

            var value = pair[(separator + 1)..];
            for (var decode = 0; decode < 3; decode++)
            {
                var decoded = WebUtility.UrlDecode(value);
                if (string.Equals(decoded, value, StringComparison.Ordinal))
                {
                    break;
                }

                value = decoded;
            }

            var matched = FindFirstUrl(value, pattern, baseUri);
            if (matched is not null
                && !string.Equals(
                    matched.AbsoluteUri,
                    wrapperUri.AbsoluteUri,
                    StringComparison.Ordinal))
            {
                mediaUri = matched;
                return true;
            }
        }

        return false;
    }

    private static bool TryCreateMediaUri(
        string value,
        Uri baseUri,
        out Uri mediaUri)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out mediaUri!)
            && !Uri.TryCreate(baseUri, value, out mediaUri!))
        {
            return false;
        }

        return mediaUri.Scheme is "http" or "https"
            && !IsIgnoredMediaUrl(mediaUri);
    }

    private static async Task<WebPageInteractionKind> DetectInteractionAsync(
        CoreWebView2 webView)
    {
        try
        {
            var json = await webView.ExecuteScriptAsync(
                """
                (() => {
                    const isVisible = element => {
                        if (!element) return false;
                        const style = getComputedStyle(element);
                        const rect = element.getBoundingClientRect();
                        return style.display !== "none"
                            && style.visibility !== "hidden"
                            && style.opacity !== "0"
                            && rect.width > 0
                            && rect.height > 0;
                    };
                    const hasVisible = selector =>
                        Array.from(document.querySelectorAll(selector))
                            .some(isVisible);
                    return {
                        url: location.href || "",
                        title: document.title || "",
                        text: (document.body?.innerText || "").slice(0, 8000),
                        hasPasswordInput:
                            hasVisible('input[type="password"]'),
                        hasChallengeElement: hasVisible(
                            'iframe[src*="captcha"],'
                            + ' iframe[src*="challenge"],'
                            + ' [class*="captcha"], [id*="captcha"],'
                            + ' [class*="turnstile"], [id*="turnstile"],'
                            + ' [class*="cf-chl"], [id*="cf-chl"]')
                    };
                })()
                """);
            var snapshot = JsonSerializer.Deserialize<WebPageSnapshot>(json);
            return snapshot is null
                ? WebPageInteractionKind.None
                : WebPageInteractionClassifier.Classify(snapshot);
        }
        catch (Exception ex)
            when (ex is COMException
                or InvalidOperationException
                or JsonException)
        {
            return WebPageInteractionKind.None;
        }
    }

    private async Task<bool> ShowVerificationWindowAsync(
        Uri uri,
        IReadOnlyDictionary<string, string> headers,
        string? cookies,
        Func<CancellationToken, Task<bool>> verifyAsync,
        CancellationToken cancellationToken)
    {
        var window = new SourceVerificationWindow(
            _environment!,
            uri,
            headers,
            cookies,
            verifyAsync);
        _verificationWindow = window;
        try
        {
            return await window.ShowAsync(cancellationToken);
        }
        finally
        {
            if (ReferenceEquals(_verificationWindow, window))
            {
                _verificationWindow = null;
            }
        }
    }

    private static bool IsMediaResponse(string contentType, bool hasRange)
        => contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
            || hasRange
            && (contentType.StartsWith(
                    "video/",
                    StringComparison.OrdinalIgnoreCase)
                || contentType.Contains(
                    "application/octet-stream",
                    StringComparison.OrdinalIgnoreCase));

    private static bool IsIgnoredMediaUrl(Uri uri)
    {
        var value = uri.AbsoluteUri;
        return value.Contains("googleads", StringComparison.OrdinalIgnoreCase)
            || value.Contains(
                "googlesyndication",
                StringComparison.OrdinalIgnoreCase)
            || value.Contains(
                "adtrafficquality",
                StringComparison.OrdinalIgnoreCase)
            || value.Contains("doubleclick", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _idleCleanupCancellation?.Cancel();
        _idleCleanupCancellation?.Dispose();
        _idleCleanupCancellation = null;
        CloseBrowserWindow();
        _gate.Dispose();
    }

    public void CloseBrowserWindow()
    {
        var verificationWindow = _verificationWindow;
        _verificationWindow = null;
        verificationWindow?.CancelAndClose();
        _idleCleanupCancellation?.Cancel();
        _idleCleanupCancellation?.Dispose();
        _idleCleanupCancellation = null;
        var controller = _controller;
        _coreWebView = null;
        _controller = null;
        _environment = null;
        if (controller is not null)
        {
            TryRunWebViewCleanup(controller.Close);
        }
    }
}

internal sealed record WebViewResolutionAttempt(
    WebResolvedMedia? Media,
    WebPageInteractionKind Interaction);

internal sealed class WebCapturedUrl
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("confirmed")]
    public bool Confirmed { get; set; }
}

internal sealed record WebResolutionRequest(
    string SourceId,
    Uri PageUri,
    string VideoUrlPattern,
    bool EnableNestedUrl,
    string? NestedUrlPattern,
    bool ScanDomMediaUrls,
    bool ScanInlineScriptUrls,
    IReadOnlyDictionary<string, string> Headers,
    string? Cookies,
    TimeSpan? SourceDeclaredTimeout = null,
    string? ActionScript = null,
    bool UseLegacyParser = false);

internal sealed record WebResolvedMedia(
    Uri Uri,
    IReadOnlyDictionary<string, string> Headers);
