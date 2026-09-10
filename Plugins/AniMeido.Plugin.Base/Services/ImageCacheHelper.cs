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
internal sealed class ImageDownloadCoordinator
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
        if (HasLocalCache(key))
            return new(ImageDownloadStatus.Success, GetLocalPath(key));

        if (_failures.TryGetValue(key, out var failedUntil))
        {
            if (failedUntil > DateTimeOffset.UtcNow)
                return new(ImageDownloadStatus.Failed, null);
            _failures.TryRemove(key, out _);
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
                ImageDownloadResult result;
                try
                {
                    result = await operation.GetTask().WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    result = new(ImageDownloadStatus.Cancelled, null);
                }
                if (result.Status == ImageDownloadStatus.Failed)
                    _failures[key] = DateTimeOffset.UtcNow + _failureCooldown;
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
            }
        }
    }

    public void Invalidate(ImageCacheKey key)
    {
        _failures.TryRemove(key, out _);
        TryDelete(GetLocalPath(key));
    }

    public void ClearAll()
    {
        Interlocked.Increment(ref _cacheGeneration);
        _failures.Clear();
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
                if (generation != CacheGeneration)
                    return DownloadAttemptResult.Cancelled;
                File.Move(tempPath, localPath, overwrite: true);
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
        private readonly CancellationTokenSource _cancellation = new();
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
                return _task ??= _factory(_cancellation.Token);
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
                _accepting = false;
            _cancellation.Cancel();
        }
    }
}

/// <summary>BasePlugin image cache facade used by XAML-created controls.</summary>
internal static class ImageCacheHelper
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AniMeido", "cache", "images");
    private static readonly HashSet<string> AllowedImageHosts = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "archive.animeido.com",
        "bgm-proxy.animeido.com",
        "lain.bgm.tv",
    };
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };
    private static readonly ImageDownloadCoordinator Coordinator = new(
        CacheDir,
        SharedHttpClient,
        uriValidator: uri => AllowedImageHosts.Contains(uri.Host));
    private static readonly SemaphoreSlim EvictionLock = new(1, 1);
    private static readonly object EvictionScheduleLock = new();
    private static CancellationTokenSource? _evictionDelayCancellation;

    public const int MaxCacheSizeMB = 500;
    public static readonly string PlaceholderPath = Path.Combine(
        AppContext.BaseDirectory,
        "Assets",
        "Placeholder_cover.png");
    public static readonly Uri PlaceholderUri = new(PlaceholderPath);

    public static string GetLocalPath(int animeId)
        => Coordinator.GetLocalPath(ImageCacheKey.Cover(animeId));

    public static string GetAvatarLocalPath(string url)
        => Coordinator.GetLocalPath(ImageCacheKey.Avatar(url));

    public static bool HasLocalCache(int animeId)
        => Coordinator.HasLocalCache(ImageCacheKey.Cover(animeId));

    public static bool HasAvatarCache(string url)
        => Coordinator.HasLocalCache(ImageCacheKey.Avatar(url));

    public static async Task<bool> CacheImageAsync(
        int animeId,
        string url,
        CancellationToken cancellationToken = default)
    {
        var result = await Coordinator.GetOrDownloadAsync(
            ImageCacheKey.Cover(animeId),
            url,
            cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
            ScheduleEviction();
        return result.IsSuccess;
    }

    public static async Task<bool> CacheAvatarAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        var result = await Coordinator.GetOrDownloadAsync(
            ImageCacheKey.Avatar(url),
            url,
            cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
            ScheduleEviction();
        return result.IsSuccess;
    }

    public static void InvalidateCover(int animeId)
        => Coordinator.Invalidate(ImageCacheKey.Cover(animeId));

    public static void InvalidateAvatar(string url)
        => Coordinator.Invalidate(ImageCacheKey.Avatar(url));

    public static void ClearAll() => Coordinator.ClearAll();

    public static (int count, double sizeMB) GetCacheStats()
    {
        var stats = Coordinator.GetCacheStats();
        return (stats.Count, stats.SizeMB);
    }

    private static void ScheduleEviction()
    {
        CancellationTokenSource delayCancellation;
        lock (EvictionScheduleLock)
        {
            _evictionDelayCancellation?.Cancel();
            _evictionDelayCancellation?.Dispose();
            delayCancellation = new CancellationTokenSource();
            _evictionDelayCancellation = delayCancellation;
        }
        _ = RunScheduledEvictionAsync(delayCancellation);
    }

    private static async Task RunScheduledEvictionAsync(
        CancellationTokenSource delayCancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), delayCancellation.Token)
                .ConfigureAwait(false);
            if (!await EvictionLock.WaitAsync(0).ConfigureAwait(false))
                return;
            try
            {
                await Coordinator.EvictIfNeededAsync(MaxCacheSizeMB * 1024L * 1024L)
                    .ConfigureAwait(false);
            }
            finally
            {
                EvictionLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (EvictionScheduleLock)
            {
                if (ReferenceEquals(_evictionDelayCancellation, delayCancellation))
                {
                    _evictionDelayCancellation = null;
                    delayCancellation.Dispose();
                }
            }
        }
    }
}
