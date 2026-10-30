using AniMeido.Plugin.Player.Views;
using AniMeido.Plugin.Player.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace AniMeido.Plugin.Player.Sources.Web;

internal sealed class WebMediaResolver : IAsyncDisposable
{
    private static readonly TimeSpan InteractionDetectionGracePeriod =
        TimeSpan.FromSeconds(2);

    private readonly HttpClient _httpClient;
    private readonly PlayerRuntimeSettingsStore _runtimeSettings;
    private readonly HostWebSessionManager _sessionManager;
    private readonly PlaybackDiagnosticRecorder _diagnostics;
    private readonly MediaProbe _mediaProbe;
    private readonly HttpPageMediaResolver _httpPageResolver;
    private readonly ILogger<WebMediaResolver> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lifecycleSync = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly WebNavigationHistory _navigationHistory = new();
    private Task _idleCleanupTask = Task.CompletedTask;
    private Task? _disposeTask;
    private readonly ConcurrentDictionary<string, byte>
        _forcedVerificationHosts = new(StringComparer.OrdinalIgnoreCase);
    private WebViewHostBinding? _binding;
    private CoreWebView2Environment? _environment;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _coreWebView;
    private bool _webViewReady;
    private SourceVerificationWindow? _verificationWindow;
    private CancellationTokenSource? _idleCleanupCancellation;
    private long _generation;
    private bool _disposed;

    public WebMediaResolver(
        HttpClient httpClient,
        PlayerRuntimeSettingsStore runtimeSettings,
        HostWebSessionManager sessionManager,
        PlaybackDiagnosticRecorder diagnostics,
        ILogger<WebMediaResolver>? logger = null)
    {
        _httpClient = httpClient;
        _runtimeSettings = runtimeSettings;
        _sessionManager = sessionManager;
        _diagnostics = diagnostics;
        _mediaProbe = new MediaProbe(httpClient, diagnostics);
        _httpPageResolver = new HttpPageMediaResolver(httpClient, diagnostics);
        _logger = logger ?? NullLogger<WebMediaResolver>.Instance;
    }

    internal WebViewHostBinding? CurrentBinding
    {
        get
        {
            lock (_lifecycleSync)
                return _binding;
        }
    }

    public void InvalidateBinding(WebViewHostBinding binding)
    {
        lock (_lifecycleSync)
            binding.Invalidate();
    }

    public async Task<bool> AttachUiThreadAsync(WebViewHostBinding binding, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        using var operation = CreateOperationCancellation(cancellationToken);
        await _gate.WaitAsync(operation.Token);
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            lock (_lifecycleSync)
            {
                if (_disposed || !binding.IsActive)
                    return false;
                if (ReferenceEquals(_binding, binding))
                    return true;
            }

            CancelIdleCleanup();
            await CloseBrowserWindowCoreAsync(CurrentBinding);
            operation.Token.ThrowIfCancellationRequested();
            lock (_lifecycleSync)
            {
                if (_disposed || !binding.IsActive)
                    return false;
                _binding = binding;
                return true;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DetachUiThreadAsync(WebViewHostBinding binding)
    {
        Task waiting;
        bool disposing;
        lock (_lifecycleSync)
        {
            binding.Invalidate();
            disposing = _disposeTask is not null;
            // 在同一个短锁内登记等待，释放流程不会越过尚未登记的解绑操作。
            waiting = _disposeTask ?? _gate.WaitAsync();
        }
        await waiting;
        if (disposing)
            return;

        try
        {
            if (!ReferenceEquals(CurrentBinding, binding))
                return;
            CancelIdleCleanup();
            await CloseBrowserWindowCoreAsync(binding);
            lock (_lifecycleSync)
                _binding = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<T> RunWithTemporaryHostAsync<T>(WebViewHostBinding binding,
        Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        using var operation = CreateOperationCancellation(cancellationToken);
        await _gate.WaitAsync(operation.Token);
        var previous = CurrentBinding;
        var borrowed = false;
        try
        {
            lock (_lifecycleSync)
                ObjectDisposedException.ThrowIf(!binding.IsActive, binding);
            CancelIdleCleanup();
            await CloseBrowserWindowCoreAsync(previous);
            operation.Token.ThrowIfCancellationRequested();
            lock (_lifecycleSync)
            {
                ObjectDisposedException.ThrowIf(_disposed || !binding.IsActive, binding);
                _binding = binding;
                borrowed = true;
            }
            return await action(operation.Token);
        }
        finally
        {
            try
            {
                if (borrowed)
                {
                    // 清理失败时不恢复旧身份；当前宿主仍持有最终解绑责任。
                    await CloseBrowserWindowCoreAsync(binding);
                    lock (_lifecycleSync)
                        _binding = !_disposed && previous is { IsActive: true } ? previous : null;
                    ScheduleIdleCleanup();
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public async Task<WebResolvedMedia> ResolveAsync(
        WebResolutionRequest request,
        CancellationToken cancellationToken)
    {
        using var operation = CreateOperationCancellation(cancellationToken);
        cancellationToken = operation.Token;
        await _gate.WaitAsync(cancellationToken);
        CancelIdleCleanup();
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
            var sessions = await _sessionManager.ListAsync(cancellationToken);
            if (sessions.ReadFailure is { } readFailure)
            {
                _logger.LogWarning(readFailure,
                    "Site session records could not be read; continuing without stored session details.");
            }
            var hostSession = sessions.Items
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
            var timeoutResult = await _runtimeSettings.GetEffectiveTimeoutAsync(
                request.SourceId,
                request.SourceDeclaredTimeout,
                cancellationToken);
            if (timeoutResult.FallbackReason is { } timeoutReadFailure)
            {
                _logger.LogWarning(timeoutReadFailure,
                    "Runtime timeout settings could not be read; continuing with default settings.");
            }
            var timeout = timeoutResult.Timeout;
            var forceVerification =
                _forcedVerificationHosts.TryRemove(normalizedHost, out _);
            var initialAttempt = forceVerification
                ? new WebViewResolutionAttempt(
                    null,
                    WebPageInteractionKind.HumanVerification)
                : await RunWithResolutionBudgetAsync(async token =>
                {
                    var attempt = await RunBackgroundAttemptCoreAsync(request, token);
                    if (attempt.Media is not null)
                    {
                        attempt = attempt with
                        {
                            Media = await _mediaProbe.ValidateAsync(
                                attempt.Media, request.PageUri, request.SourceId, token),
                        };
                    }
                    return attempt;
                },
                    request,
                    timeout,
                    cancellationToken);
            if (initialAttempt.Media is not null)
            {
                var validated = initialAttempt.Media;
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

            var validatedAfterVerification = await CompleteVerificationAsync(
                request, verifiedMedia, timeout, cancellationToken);
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
            ScheduleIdleCleanup();
            _gate.Release();
        }
    }

    private CancellationTokenSource CreateOperationCancellation(CancellationToken cancellationToken)
    {
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _shutdown.Token);
        }
    }

    private void CancelIdleCleanup()
    {
        lock (_lifecycleSync)
        {
            _idleCleanupCancellation?.Cancel();
        }
    }

    private void ScheduleIdleCleanup()
    {
        lock (_lifecycleSync)
        {
            if (_disposed)
            {
                return;
            }

            _idleCleanupCancellation?.Cancel();
            var cancellation = new CancellationTokenSource();
            _idleCleanupCancellation = cancellation;
            var previousCleanup = _idleCleanupTask;
            var cleanup = CleanupWhenIdleAsync(cancellation);
            // 取消不代表旧任务已经退出；释放仍需等待它完成最后的收尾。
            _idleCleanupTask = previousCleanup.IsCompletedSuccessfully
                ? cleanup
                : Task.WhenAll(previousCleanup, cleanup);
        }
    }

    private async Task CleanupWhenIdleAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(5), cancellation.Token);
            try
            {
                await _gate.WaitAsync(cancellation.Token);
            }
            catch (ObjectDisposedException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            try
            {
                var binding = CurrentBinding;
                if (binding is null)
                    return;
                await binding.InvokeAsync(() =>
                {
                    bool canClean;
                    lock (_lifecycleSync)
                    {
                        canClean = !_disposed && !cancellation.IsCancellationRequested
                            && ReferenceEquals(_idleCleanupCancellation, cancellation);
                    }
                    if (canClean)
                        CloseHiddenController();
                    return Task.CompletedTask;
                }, cancellation.Token);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Idle browser cleanup failed; retaining its owner for retry.");
            _diagnostics.Record("webview", "idle-cleanup-failed",
                data: new Dictionary<string, object?> { ["message"] = ex.Message });
        }
        finally
        {
            lock (_lifecycleSync)
            {
                if (ReferenceEquals(_idleCleanupCancellation, cancellation))
                {
                    _idleCleanupCancellation = null;
                }
                cancellation.Dispose();
            }
        }
    }

    private void CloseHiddenController()
    {
        // Close 失败仍由当前绑定持有控制器，后续解绑可重试。
        _controller?.Close();
        if (_coreWebView is { } webView)
        {
            TryRunWebViewCleanup(() =>
            {
                webView.NavigationStarting -= OnHiddenNavigationStarting;
                webView.NavigationCompleted -= OnHiddenNavigationCompleted;
            });
        }

        _navigationHistory.Clear();
        _coreWebView = null;
        _controller = null;
        _webViewReady = false;
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

    public void RequireVerificationOnNextResolve(Uri pageUri)
    {
        ArgumentNullException.ThrowIfNull(pageUri);
        _forcedVerificationHosts[
            HostWebSessionManager.NormalizeHost(pageUri.Host)] = 0;
    }

    private Task<WebViewResolutionAttempt> RunBackgroundAttemptAsync(
        WebResolutionRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        => RunWithResolutionBudgetAsync(
            token => RunBackgroundAttemptCoreAsync(request, token),
            request, timeout, cancellationToken);

    private async Task<T> RunWithResolutionBudgetAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        WebResolutionRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        try
        {
            return await operation(timeoutCancellation.Token);
        }
        catch (OperationCanceledException ex)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new SourceResolutionException(
                SourceResolutionFailureKind.Timeout,
                timeoutCancellation.IsCancellationRequested
                    ? $"播放源解析超过 {timeout.TotalSeconds:0} 秒。"
                    : $"播放源请求超过 {_httpClient.Timeout.TotalSeconds} 秒未响应。",
                request.PageUri,
                ex);
        }
    }

    private async Task<WebViewResolutionAttempt> RunBackgroundAttemptCoreAsync(
        WebResolutionRequest request,
        CancellationToken cancellationToken)
    {
        WebResolvedMedia? direct = null;
        try
        {
            direct = await _httpPageResolver.TryResolveAsync(request, cancellationToken);
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
            // 浏览器配置可能包含 HTTP 请求缺少的会话，让 WebView2 继续分类。
        }
        if (direct is not null)
        {
            return new WebViewResolutionAttempt(direct, WebPageInteractionKind.None);
        }
        return await RunOnUiAsync(
            () => TryResolveWithWebViewOnUiAsync(request, cancellationToken),
            cancellationToken);
    }

    public async Task<string> LoadPageHtmlAsync(
        Uri uri,
        bool interactive,
        CancellationToken cancellationToken)
    {
        using var operation = CreateOperationCancellation(cancellationToken);
        cancellationToken = operation.Token;
        await _gate.WaitAsync(cancellationToken);
        CancelIdleCleanup();
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
            ScheduleIdleCleanup();
            _gate.Release();
        }
    }

    internal async Task<WebResolvedMedia> CompleteVerificationAsync(
        WebResolutionRequest request,
        WebResolvedMedia verifiedMedia,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _sessionManager.RecordVerifiedAsync(
                request.PageUri.Host,
                request.SourceId,
                verifiedMedia.Headers.GetValueOrDefault("User-Agent"),
                cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException
            or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex,
                "Could not save the verified site session for {Host} and {SourceId}; continuing playback.",
                request.PageUri.Host, request.SourceId);
        }

        // 人工验证及其重试不计入随后媒体探测的预算；保存记录失败也不能跳过探测。
        return await RunWithResolutionBudgetAsync(
            token => _mediaProbe.ValidateAsync(
                verifiedMedia, request.PageUri, request.SourceId, token),
            request, timeout, cancellationToken);
    }

    public Task<HostWebSessionReadResult> ListSessionsAsync(
        CancellationToken cancellationToken)
        => _sessionManager.ListAsync(cancellationToken);

    public Task<HostWebSessionClearResult> ClearSessionAsync(
        string? host, WebViewHostBinding binding, CancellationToken cancellationToken)
        => RunWithTemporaryHostAsync(binding, token => ClearSessionOnHostAsync(host, token), cancellationToken);

    private async Task<HostWebSessionClearResult> ClearSessionOnHostAsync(string? host, CancellationToken cancellationToken)
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
                var normalizedHost = HostWebSessionManager.NormalizeHost(host);
                var hosts = new[] { normalizedHost, $"www.{normalizedHost}" };
                foreach (var scheme in new[] { "https", "http" })
                {
                    foreach (var candidateHost in hosts)
                    {
                        var cookies = await cookieManager.GetCookiesAsync($"{scheme}://{candidateHost}/");
                        foreach (var cookie in cookies)
                            cookieManager.DeleteCookie(cookie);
                    }
                }
            }
            return true;
        }, cancellationToken);
        return await CompleteSessionClearAsync(host, cancellationToken);
    }

    internal async Task<HostWebSessionClearResult> CompleteSessionClearAsync(
        string? host,
        CancellationToken cancellationToken)
    {
        try
        {
            await _sessionManager.RemoveMetadataAsync(host, cancellationToken);
            return new HostWebSessionClearResult(MetadataFailure: null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException
            or IOException or UnauthorizedAccessException)
        {
            // Cookie 已由前一阶段清除；交给界面报告部分完成，不再误报完全失败。
            return new HostWebSessionClearResult(ex);
        }
    }

    private async Task<WebViewResolutionAttempt> TryResolveWithWebViewOnUiAsync(
        WebResolutionRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureWebViewAsync(cancellationToken);
        var generation = Interlocked.Increment(ref _generation);
        var navigationCallbacks = new WebNavigationCallbackScope();
        using var captureCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var completion = new TaskCompletionSource<Uri>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var interactionCompletion =
            new TaskCompletionSource<WebPageInteractionKind>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyDictionary<string, string>? capturedMediaHeaders = null;
        var nestedNavigated = false;
        var actionExecuted = false;

        bool IsCurrentAttempt()
            => navigationCallbacks.IsAccepting
                && !captureCancellation.IsCancellationRequested
                && generation == Volatile.Read(ref _generation);

        bool CanContinueNavigation()
            => IsCurrentAttempt()
                && !completion.Task.IsCompleted
                && !interactionCompletion.Task.IsCompleted;

        void OnResponse(
            CoreWebView2 sender,
            CoreWebView2WebResourceResponseReceivedEventArgs args)
        {
            if (!IsCurrentAttempt())
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
                    && WebMediaUrlParser.TryMatchObservedMediaUrl(
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
                    && !WebMediaUrlParser.IsIgnoredMediaUrl(mediaUri))
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
            if (!IsCurrentAttempt())
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
                        && WebMediaUrlParser.TryCreateMediaUri(
                            candidate.Url,
                            messageBaseUri,
                            out var mediaUri))
                    || WebMediaUrlParser.TryMatchObservedMediaUrl(
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
            using var registration = navigationCallbacks.TryEnter();
            if (registration is null || !CanContinueNavigation())
            {
                return;
            }

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
            if (!args.IsSuccess)
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
                        if (!CanContinueNavigation())
                        {
                            return;
                        }
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
                        if (!CanContinueNavigation())
                        {
                            return;
                        }
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
                    if (!CanContinueNavigation())
                    {
                        return;
                    }
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
                if (!CanContinueNavigation())
                {
                    return;
                }
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
                var mediaUri = WebMediaUrlParser.FindMedia(
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
                if (!CanContinueNavigation())
                {
                    return;
                }
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
                        captureCancellation.Token);
                    if (CanContinueNavigation())
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
        var domPolling = Task.CompletedTask;
        webView.WebResourceResponseReceived += OnResponse;
        webView.WebMessageReceived += OnWebMessageReceived;
        webView.NavigationCompleted += OnNavigationCompleted;
        try
        {
            Navigate(webView, request.PageUri, request.Headers, request.Cookies);
            domPolling = PollForMediaAsync(
                webView,
                request,
                completion,
                headers => capturedMediaHeaders = headers,
                captureCancellation.Token);
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
            navigationCallbacks.StopAccepting();
            TryRunWebViewCleanup(() =>
            {
                webView.WebResourceResponseReceived -= OnResponse;
                webView.WebMessageReceived -= OnWebMessageReceived;
                webView.NavigationCompleted -= OnNavigationCompleted;
            });
            captureCancellation.Cancel();
            try
            {
                // 原生脚本不能被取消；真正退出前继续持有 WebView 和外层解析资格。
                await Task.WhenAll(domPolling, navigationCallbacks.WaitForDrainAsync());
            }
            finally
            {
                TryRunWebViewCleanup(() => ResetWebView(webView));
            }
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
        var webView = _coreWebView!;
        var observation = _navigationHistory.BeginObservation((success, errorStatus) =>
        {
            if (success)
            {
                navigation.TrySetResult();
            }
            else
            {
                navigation.TrySetException(new InvalidOperationException(
                    $"WebView2 页面加载失败：{errorStatus}"));
            }
        });

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
            _navigationHistory.EndObservation(observation);
            TryRunWebViewCleanup(() => ResetWebView(webView));
        }
    }

    private async Task<bool> VerifyPageAccessOnUiAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        await EnsureWebViewAsync(cancellationToken);
        var navigation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var webView = _coreWebView!;
        var observation = _navigationHistory.BeginObservation((_, _) => navigation.TrySetResult());
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
            _navigationHistory.EndObservation(observation);
            TryRunWebViewCleanup(() => ResetWebView(webView));
        }
    }

    private async Task<T> RunOnUiAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var binding = RequireActiveBinding();
        return await binding.InvokeAsync(operation, cancellationToken);
    }

    private WebViewHostBinding RequireActiveBinding()
    {
        lock (_lifecycleSync)
        {
            if (_binding is null)
                throw new InvalidOperationException("网页解析器尚未绑定播放器 UI 线程。");
            ObjectDisposedException.ThrowIf(!_binding.IsActive, _binding);
            return _binding;
        }
    }

    private async Task EnsureWebViewAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_webViewReady)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        // 所有调用均由绑定的 UI 派发包围；等待真正创建完成后才能交还解析资格。
        await CreateWebViewAsync(RequireActiveBinding().WindowHandle);
    }

    private async Task CreateWebViewAsync(nint hostWindowHandle)
    {
        if (_webViewReady)
        {
            return;
        }

        if (_controller is not null)
        {
            CloseHiddenController();
        }

        _environment ??= await _sessionManager.GetEnvironmentAsync();
        var parentWindow =
            CoreWebView2ControllerWindowReference.CreateFromWindowHandle(
                unchecked((ulong)hostWindowHandle.ToInt64()));
        var controller = await _environment.CreateCoreWebView2ControllerAsync(
            parentWindow);
        _controller = controller;
        try
        {
            controller.Bounds = new Windows.Foundation.Rect(0, 0, 1, 1);
            controller.IsVisible = false;
            _coreWebView = controller.CoreWebView2;
            _coreWebView.NavigationStarting += OnHiddenNavigationStarting;
            _coreWebView.NavigationCompleted += OnHiddenNavigationCompleted;
            await _coreWebView.AddScriptToExecuteOnDocumentCreatedAsync(
                WebMediaCaptureScripts.Install);
            _coreWebView.AddWebResourceRequestedFilter(
                "*",
                CoreWebView2WebResourceContext.All);
            _webViewReady = true;
        }
#pragma warning disable CA1031 // 初始化失败仍须收尾；关闭失败保留资源引用和两个原始异常。
        catch (Exception initializationFailure)
        {
            try
            {
                CloseHiddenController();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(initializationFailure, cleanupFailure);
            }

            throw;
        }
#pragma warning restore CA1031
    }

    private void OnHiddenNavigationStarting(
        CoreWebView2 sender,
        CoreWebView2NavigationStartingEventArgs args)
    {
        if (!ReferenceEquals(sender, _coreWebView))
        {
            return;
        }

        _navigationHistory.RecordStarting(args.NavigationId);
    }

    private void OnHiddenNavigationCompleted(
        CoreWebView2 sender,
        CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!ReferenceEquals(sender, _coreWebView))
        {
            return;
        }

        _navigationHistory.Complete(
            args.NavigationId,
            args.IsSuccess,
            args.WebErrorStatus.ToString());
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
        while (!completion.Task.IsCompleted
            && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var json = await webView.ExecuteScriptAsync(
                    WebMediaCaptureScripts.Poll);
                if (cancellationToken.IsCancellationRequested || completion.Task.IsCompleted)
                {
                    return;
                }
                var candidates =
                    JsonSerializer.Deserialize<WebCapturedUrl[]>(json) ?? [];
                foreach (var candidate in candidates)
                {
                    var matched = (candidate.Confirmed
                            && WebMediaUrlParser.TryCreateMediaUri(
                                candidate.Url,
                                request.PageUri,
                                out var confirmedUri))
                        || WebMediaUrlParser.TryMatchObservedMediaUrl(
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
        var headers = HeaderNormalizer.BuildMediaHeaders(request, request.PageUri);
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

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_lifecycleSync)
        {
            if (_disposeTask is not null)
                return new ValueTask(_disposeTask);
            _disposed = true;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
        }
        _ = CompleteDisposeAsync(completion);
        return new ValueTask(completion.Task);
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource completion)
    {
        try
        {
            try
            {
                await _shutdown.CancelAsync();
            }
            finally
            {
                CancelIdleCleanup();
                await DisposeCoreAsync();
            }
            completion.TrySetResult();
        }
#pragma warning disable CA1031 // 先公布共享释放任务，再交还真实清理结果。
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
#pragma warning restore CA1031
    }

    private async Task DisposeCoreAsync()
    {
        await CloseBrowserWindowAsync();
        await _idleCleanupTask;
        lock (_lifecycleSync)
        {
            _binding?.Invalidate();
            _binding = null;
        }
        _shutdown.Dispose();
        _gate.Dispose();
    }

    public async Task CloseBrowserWindowAsync()
    {
        await _gate.WaitAsync();
        try
        {
            CancelIdleCleanup();
            await CloseBrowserWindowCoreAsync(CurrentBinding);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task CloseBrowserWindowCoreAsync(WebViewHostBinding? binding)
        => binding is null ? Task.CompletedTask : binding.InvokeAsync(() =>
        {
            CloseBrowserWindow();
            return Task.CompletedTask;
        }, CancellationToken.None);

    private void CloseBrowserWindow()
    {
        CancelIdleCleanup();
        _verificationWindow?.CancelAndClose();
        CloseHiddenController();
        _verificationWindow = null;
        _environment = null;
    }
}

internal sealed record WebViewResolutionAttempt(
    WebResolvedMedia? Media,
    WebPageInteractionKind Interaction);

internal sealed record HostWebSessionClearResult(Exception? MetadataFailure);

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
