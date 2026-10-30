namespace AniMeido.Plugin.Base.Services;

/// <summary>XAML 创建的控件共用此图片缓存，生命周期由 Base 插件持有。</summary>
internal static class ImageCacheHelper
{
    internal static string CacheDirectory => Path.Combine(AppDataRoots.Roaming, "cache", "images");
    private static readonly HashSet<string> AllowedImageHosts = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "img.animeido.com",
    };
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };
    // 仅查询默认路径时不创建缓存目录；真正使用缓存时再初始化下载器。
    private static readonly Lazy<ImageDownloadCoordinator> DownloadCoordinator = new(() => new(
        CacheDirectory,
        SharedHttpClient,
        uriValidator: uri => AllowedImageHosts.Contains(uri.Host)));
    private static ImageDownloadCoordinator Coordinator
    {
        get
        {
            lock (EvictionScheduleLock)
            {
                ObjectDisposedException.ThrowIf(_stopTask is not null, typeof(ImageCacheHelper));
                return DownloadCoordinator.Value;
            }
        }
    }
    private static readonly SemaphoreSlim EvictionLock = new(1, 1);
    private static readonly object EvictionScheduleLock = new();
    private static CancellationTokenSource? _evictionDelayCancellation;
    private static readonly HashSet<Task> EvictionTasks = [];
    private static Task? _stopTask;

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
        lock (EvictionScheduleLock)
        {
            if (_stopTask is not null)
                return;
            _evictionDelayCancellation?.Cancel();
            var delayCancellation = new CancellationTokenSource();
            _evictionDelayCancellation = delayCancellation;
            EvictionTasks.RemoveWhere(task => task.IsCompleted);
            EvictionTasks.Add(RunScheduledEvictionAsync(delayCancellation, delayCancellation.Token));
        }
    }

    internal static ValueTask StopAsync()
    {
        lock (EvictionScheduleLock)
            return new ValueTask(_stopTask ??= StopCoreAsync());
    }

    private static async Task StopCoreAsync()
    {
        _evictionDelayCancellation?.Cancel();
        var downloads = DownloadCoordinator.IsValueCreated
            ? DownloadCoordinator.Value.DisposeAsync().AsTask()
            : Task.CompletedTask;
        await Task.WhenAll(EvictionTasks.Append(downloads)).ConfigureAwait(false);
        SharedHttpClient.Dispose();
        EvictionLock.Dispose();
        lock (EvictionScheduleLock)
            EvictionTasks.Clear();
    }

    private static async Task RunScheduledEvictionAsync(
        CancellationTokenSource delayCancellation,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)
                .ConfigureAwait(false);
            if (!await EvictionLock.WaitAsync(0).ConfigureAwait(false))
                return;
            try
            {
                // 计划清理时下载器已经存在；退出等待本任务，不再通过准入入口取实例。
                await DownloadCoordinator.Value.EvictIfNeededAsync(MaxCacheSizeMB * 1024L * 1024L)
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
#pragma warning disable CA1031 // 后台清理任务必须就地观察异常。
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError(
                "Image cache eviction failed: {0}", ex);
        }
#pragma warning restore CA1031
        finally
        {
            lock (EvictionScheduleLock)
            {
                if (ReferenceEquals(_evictionDelayCancellation, delayCancellation))
                {
                    _evictionDelayCancellation = null;
                }
            }
            delayCancellation.Dispose();
        }
    }
}
