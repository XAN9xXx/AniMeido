using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace AniMeido.Plugin.Base.Services;

internal readonly record struct ImageCacheKey(string Category, string Value)
{
    public static ImageCacheKey Cover(int animeId) => new("cover", animeId.ToString());

    public static ImageCacheKey Avatar(string url) => new("avatar", NormalizeUrl(url));

    private static string NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url.Trim();

        return uri.GetComponents(
            UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
            UriFormat.UriEscaped);
    }
}

internal enum ImageDownloadStatus
{
    Success,
    Failed,
    Cancelled,
}

internal readonly record struct ImageDownloadResult(
    ImageDownloadStatus Status,
    string? LocalPath)
{
    public bool IsSuccess => Status == ImageDownloadStatus.Success;
}

/// <summary>
/// Coordinates bounded, shared and cancellation-aware image downloads.
/// </summary>
internal sealed class ImageDownloadCoordinator : IAsyncDisposable
{
    private const long MaxImageBytes = 5 * 1024 * 1024;
    private readonly string _cacheRoot;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _downloadThrottle;
    private readonly TimeSpan _retryDelay;
    private readonly TimeSpan _failureCooldown;
    private readonly Func<Uri, bool> _uriValidator;
    private readonly ConcurrentDictionary<ImageCacheKey, DownloadOperation> _operations = [];
    private readonly ConcurrentDictionary<ImageCacheKey, DateTimeOffset> _failures = [];
    private readonly object _failureGate = new();
    private readonly PriorityQueue<ImageCacheKey, DateTimeOffset> _failureExpirations = new();
    private readonly object _lifetimeGate = new();
    private readonly HashSet<DownloadOperation> _activeDownloads = [];
    private Task? _stopTask;
    private int _cacheGeneration;

    public ImageDownloadCoordinator(
        string cacheRoot,
        HttpClient httpClient,
        int maxConcurrency = 4,
        TimeSpan? retryDelay = null,
        TimeSpan? failureCooldown = null,
        Func<Uri, bool>? uriValidator = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        _cacheRoot = cacheRoot;
        _httpClient = httpClient;
        _downloadThrottle = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(1);
        _failureCooldown = failureCooldown ?? TimeSpan.FromSeconds(30);
        _uriValidator = uriValidator ?? (_ => true);
        Directory.CreateDirectory(_cacheRoot);
        Directory.CreateDirectory(Path.Combine(_cacheRoot, "avatars"));
    }

    public string GetLocalPath(ImageCacheKey key) => key.Category switch
    {
        "cover" => Path.Combine(_cacheRoot, $"{key.Value}.jpg"),
        "avatar" => Path.Combine(
            _cacheRoot,
            "avatars",
            $"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.Value)))}.img"),
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    public bool HasLocalCache(ImageCacheKey key)
    {
        var path = GetLocalPath(key);
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public async Task<ImageDownloadResult> GetOrDownloadAsync(
        ImageCacheKey key,
        string url,
        CancellationToken cancellationToken = default)
    {
        var generation = CacheGeneration;
        RemoveExpiredFailures();
        if (HasLocalCache(key))
            return new(ImageDownloadStatus.Success, GetLocalPath(key));

        if (_failures.TryGetValue(key, out var failedUntil))
        {
            if (failedUntil > DateTimeOffset.UtcNow)
                return new(ImageDownloadStatus.Failed, null);
            _failures.TryRemove(KeyValuePair.Create(key, failedUntil));
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operation = _operations.GetOrAdd(
                key,
                static (cacheKey, state) => new DownloadOperation(
                    token => state.Owner.DownloadWithRetryAsync(
                        cacheKey,
                        state.Url,
                        state.Owner.CacheGeneration,
                        token)),
                (Owner: this, Url: url));

            if (!operation.TryAcquire())
            {
                _operations.TryRemove(
                    new KeyValuePair<ImageCacheKey, DownloadOperation>(key, operation));
                continue;
            }

            try
            {
                Task<ImageDownloadResult> download;
                lock (_lifetimeGate)
                {
                    if (_stopTask is not null)
                        return new(ImageDownloadStatus.Cancelled, null);

                    // 取消等待的页面可以先离开，但实际下载仍由协调器持有到结束。
                    _activeDownloads.RemoveWhere(active => active.IsCompleted);
                    _activeDownloads.Add(operation);
                    download = operation.GetTask();
                }
                ImageDownloadResult result;
                try
                {
                    result = await download.WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    result = new(ImageDownloadStatus.Cancelled, null);
                }
                if (result.Status == ImageDownloadStatus.Failed)
                    RecordFailure(key, generation);
                return result;
            }
            finally
            {
                if (operation.Release())
                {
                    _operations.TryRemove(
                        new KeyValuePair<ImageCacheKey, DownloadOperation>(key, operation));
                    operation.Cancel();
                }
                else if (operation.IsCompleted)
                {
                    _operations.TryRemove(
                        new KeyValuePair<ImageCacheKey, DownloadOperation>(key, operation));
                }
                lock (_lifetimeGate)
                {
                    if (operation.IsCompleted)
                        _activeDownloads.Remove(operation);
                }
            }
        }
    }

    public void Invalidate(ImageCacheKey key)
    {
        _failures.TryRemove(key, out _);
        TryDelete(GetLocalPath(key));
    }

    private void RecordFailure(ImageCacheKey key, int generation)
    {
        lock (_failureGate)
        {
            // 清空后的旧请求不能恢复已经移除的失败冷却。
            if (generation != CacheGeneration)
                return;

            var until = DateTimeOffset.UtcNow + _failureCooldown;
            _failures[key] = until;
            _failureExpirations.Enqueue(key, until);
        }
    }

    private void RemoveExpiredFailures()
    {
        lock (_failureGate)
        {
            var now = DateTimeOffset.UtcNow;
            while (_failureExpirations.TryPeek(out _, out var until) && until <= now)
            {
                var key = _failureExpirations.Dequeue();
                // 同一作品可能已有更新的失败记录，旧到期项不能移除它。
                _failures.TryRemove(KeyValuePair.Create(key, until));
            }
        }
    }

    private void ClearFailures()
    {
        lock (_failureGate)
        {
            _failures.Clear();
            _failureExpirations.Clear();
        }
    }

    public void ClearAll()
    {
        lock (_lifetimeGate)
        {
            Interlocked.Increment(ref _cacheGeneration);
            ClearFailures();
            foreach (var pair in _operations.ToArray())
            {
                if (_operations.TryRemove(pair))
                    pair.Value.Cancel();
            }

            if (!Directory.Exists(_cacheRoot))
                return;

            foreach (var path in EnumerateCacheFiles())
                TryDelete(path);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifetimeGate)
            return new ValueTask(_stopTask ??= StopDownloadsAsync());
    }

    private async Task StopDownloadsAsync()
    {
        Interlocked.Increment(ref _cacheGeneration);
        var active = _activeDownloads.ToArray();
        foreach (var operation in active)
            operation.Cancel();
        // 下载结果仍交给各订阅者；此处只等待取消后的实际 I/O 与令牌收尾。
        Task completed = Task.WhenAll(active.Select(operation => operation.GetTask()));
        await completed.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _downloadThrottle.Dispose();
        lock (_lifetimeGate)
        {
            _activeDownloads.Clear();
            _operations.Clear();
            ClearFailures();
        }
    }

    public (int Count, double SizeMB) GetCacheStats()
    {
        long bytes = 0;
        int count = 0;
        foreach (var path in EnumerateCacheFiles())
        {
            try
            {
                bytes += new FileInfo(path).Length;
                count++;
            }
            catch (IOException)
            {
            }
        }
        return (count, bytes / 1024d / 1024d);
    }

    public async Task EvictIfNeededAsync(long maxBytes)
    {
        var files = EnumerateCacheFiles()
            .Select(path => new FileInfo(path))
            .Where(info => info.Exists)
            .OrderBy(info => info.LastWriteTimeUtc)
            .ToArray();
        var totalBytes = files.Sum(info => info.Length);
        if (totalBytes <= maxBytes)
            return;

        var targetBytes = (long)(maxBytes * 0.8);
        foreach (var info in files)
        {
            if (totalBytes <= targetBytes)
                break;
            try
            {
                File.Delete(info.FullName);
                totalBytes -= info.Length;
            }
            catch (IOException)
            {
            }
            await Task.Yield();
        }
    }

    internal int CacheGeneration => Volatile.Read(ref _cacheGeneration);

    private async Task<ImageDownloadResult> DownloadWithRetryAsync(
        ImageCacheKey key,
        string url,
        int generation,
        CancellationToken cancellationToken)
    {
        var first = await DownloadOnceAsync(key, url, generation, cancellationToken)
            .ConfigureAwait(false);
        if (first != DownloadAttemptResult.TransientFailure)
            return ToResult(first, key);

        await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
        var second = await DownloadOnceAsync(key, url, generation, cancellationToken)
            .ConfigureAwait(false);
        return ToResult(second, key);
    }

    private async Task<DownloadAttemptResult> DownloadOnceAsync(
        ImageCacheKey key,
        string url,
        int generation,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !_uriValidator(uri))
        {
            return DownloadAttemptResult.TerminalFailure;
        }

        await _downloadThrottle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (HasLocalCache(key))
                return DownloadAttemptResult.Success;

            using var response = await _httpClient.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return response.StatusCode == HttpStatusCode.RequestTimeout
                    || response.StatusCode == HttpStatusCode.TooManyRequests
                    || (int)response.StatusCode >= 500
                    ? DownloadAttemptResult.TransientFailure
                    : DownloadAttemptResult.TerminalFailure;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (string.Equals(
                    contentType,
                    "image/svg+xml",
                    StringComparison.OrdinalIgnoreCase))
            {
                return DownloadAttemptResult.TransientFailure;
            }

            if (contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true
                || response.Content.Headers.ContentLength > MaxImageBytes)
            {
                return DownloadAttemptResult.TerminalFailure;
            }

            var localPath = GetLocalPath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            var tempPath = $"{localPath}.tmp.{Guid.NewGuid():N}";
            try
            {
                long totalRead = 0;
                {
                    await using var source = await response.Content
                        .ReadAsStreamAsync(cancellationToken)
                        .ConfigureAwait(false);
                    await using var destination = new FileStream(
                        tempPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        81920,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken)
                        .ConfigureAwait(false)) > 0)
                    {
                        totalRead += read;
                        if (totalRead > MaxImageBytes)
                            return DownloadAttemptResult.TerminalFailure;
                        await destination.WriteAsync(
                            buffer.AsMemory(0, read),
                            cancellationToken).ConfigureAwait(false);
                    }

                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                if (totalRead == 0)
                    return DownloadAttemptResult.TerminalFailure;
                lock (_lifetimeGate)
                {
                    if (generation != CacheGeneration)
                        return DownloadAttemptResult.Cancelled;
                    File.Move(tempPath, localPath, overwrite: true);
                }
                return DownloadAttemptResult.Success;
            }
            finally
            {
                TryDelete(tempPath);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DownloadAttemptResult.Cancelled;
        }
        catch (TaskCanceledException)
        {
            return DownloadAttemptResult.TransientFailure;
        }
        catch (HttpRequestException)
        {
            return DownloadAttemptResult.TransientFailure;
        }
        catch (IOException)
        {
            return DownloadAttemptResult.TransientFailure;
        }
        finally
        {
            _downloadThrottle.Release();
        }
    }

    private ImageDownloadResult ToResult(
        DownloadAttemptResult result,
        ImageCacheKey key)
    {
        return result switch
        {
            DownloadAttemptResult.Success => new(
                ImageDownloadStatus.Success,
                GetLocalPath(key)),
            DownloadAttemptResult.Cancelled => new(ImageDownloadStatus.Cancelled, null),
            _ => new(ImageDownloadStatus.Failed, null),
        };
    }

    private IEnumerable<string> EnumerateCacheFiles()
    {
        if (!Directory.Exists(_cacheRoot))
            return [];
        return Directory.EnumerateFiles(_cacheRoot, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).Contains(".tmp.", StringComparison.Ordinal));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private enum DownloadAttemptResult
    {
        Success,
        TransientFailure,
        TerminalFailure,
        Cancelled,
    }

    private sealed class DownloadOperation
    {
        private readonly Func<CancellationToken, Task<ImageDownloadResult>> _factory;
        private CancellationTokenSource? _cancellation;
        private readonly object _gate = new();
        private Task<ImageDownloadResult>? _task;
        private int _subscribers;
        private bool _accepting = true;

        public DownloadOperation(Func<CancellationToken, Task<ImageDownloadResult>> factory)
            => _factory = factory;

        public bool IsCompleted
        {
            get
            {
                lock (_gate)
                    return _task?.IsCompleted == true;
            }
        }

        public bool TryAcquire()
        {
            lock (_gate)
            {
                if (!_accepting)
                    return false;
                _subscribers++;
                return true;
            }
        }

        public Task<ImageDownloadResult> GetTask()
        {
            lock (_gate)
                return _task ??= RunAsync();
        }

        private async Task<ImageDownloadResult> RunAsync()
        {
            if (!_accepting)
                return new(ImageDownloadStatus.Cancelled, null);

            // GetOrAdd 可能构造未采用的操作；只有实际启动的任务才创建并拥有令牌。
            var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            try
            {
                return await _factory(cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return new(ImageDownloadStatus.Cancelled, null);
            }
#pragma warning disable CA1031 // 所有订阅者离开后，实际下载仍须由操作所有者观察异常。
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning("Image download failed: {0}", ex);
                return new(ImageDownloadStatus.Failed, null);
            }
#pragma warning restore CA1031
            finally
            {
                lock (_gate)
                {
                    _accepting = false;
                    _cancellation = null;
                    cancellation.Dispose();
                }
            }
        }

        public bool Release()
        {
            lock (_gate)
            {
                _subscribers--;
                if (_subscribers != 0 || _task?.IsCompleted == true)
                    return false;
                _accepting = false;
                return true;
            }
        }

        public void Cancel()
        {
            lock (_gate)
            {
                _accepting = false;
                _cancellation?.Cancel();
            }
        }
    }
}
