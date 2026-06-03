using System.Collections.Concurrent;

namespace AniMeido.Plugin.Base.Services
{
    /// <summary>
    /// 番剧封面图本地缓存辅助类。
    /// 图片保存在 %AppData%/AniMeido/cache/images/{animeId}.jpg。
    /// 下载时校验 URL scheme、Content-Type 和单文件大小上限。
    /// 当总大小超过 MaxCacheSizeMB 时，自动淘汰最旧的文件。
    /// </summary>
    internal static class ImageCacheHelper
    {
        private static readonly string CacheDir;
        private static readonly SemaphoreSlim DownloadThrottle = new(4, 4);
        private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly ConcurrentDictionary<int, byte> _cachedIds = new();
        private static readonly SemaphoreSlim _evictionLock = new(1, 1);

        /// <summary>图片缓存大小上限（MB）。超过时淘汰最旧文件。</summary>
        public const int MaxCacheSizeMB = 500;

        /// <summary>单张图片最大字节数（5 MB）。</summary>
        private const long MaxImageBytes = 5 * 1024 * 1024;

        /// <summary>占位图 URI</summary>
        public static readonly Uri PlaceholderUri;

        /// <summary>允许下载图片的可信 Host 列表。</summary>
        private static readonly HashSet<string> AllowedImageHosts = new(StringComparer.OrdinalIgnoreCase)
        {
            "bgm-proxy.animeido.com",
            "lain.bgm.tv",
        };

        static ImageCacheHelper()
        {
            // 使用 AppData 路径，确保可写且不被升级覆盖
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AniMeido", "cache", "images");
            CacheDir = appData;
            Directory.CreateDirectory(CacheDir);

            var placeholderPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Placeholder_cover.png");
            PlaceholderUri = new Uri(placeholderPath);

            // 启动时预热缓存列表
            if (Directory.Exists(CacheDir))
            {
                foreach (var f in Directory.GetFiles(CacheDir, "*.jpg"))
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    if (int.TryParse(name, out var id))
                        _cachedIds.TryAdd(id, 0);
                }
            }
        }

        /// <summary>获取本地缓存路径（不保证文件存在）。</summary>
        public static string GetLocalPath(int animeId)
            => Path.Combine(CacheDir, $"{animeId}.jpg");

        /// <summary>检查本地是否有已缓存的图片。</summary>
        public static bool HasLocalCache(int animeId)
            => _cachedIds.ContainsKey(animeId);

        /// <summary>获取用于 Image.Source 的 URI。</summary>
        public static Uri GetImageUri(int animeId, string? originalUrl)
        {
            if (_cachedIds.ContainsKey(animeId))
                return new Uri(GetLocalPath(animeId));

            if (!string.IsNullOrEmpty(originalUrl) && TryCreateValidImageUri(originalUrl, out var uri))
                return uri;

            return PlaceholderUri;
        }

        /// <summary>
        /// 校验并创建合法的图片 URI。与 CacheImageAsync 共享同一校验逻辑。
        /// </summary>
        public static bool TryCreateValidImageUri(string url, out Uri uri)
        {
            uri = null!;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
                return false;
            if (parsed.Scheme != Uri.UriSchemeHttps)
                return false;
            if (!AllowedImageHosts.Contains(parsed.Host))
                return false;
            uri = parsed;
            return true;
        }

        /// <summary>
        /// 从网络 URL 下载图片并保存到本地缓存。
        /// 校验 URL scheme、Content-Type 和大小上限。
        /// 使用流式下载，避免一次性读入大文件到内存。
        /// </summary>
        public static async Task CacheImageAsync(int animeId, string url)
        {
            if (_cachedIds.ContainsKey(animeId)) return;

            await DownloadThrottle.WaitAsync();
            try
            {
                if (_cachedIds.ContainsKey(animeId)) return;

                // 校验 URL（与 GetImageUri 共享同一逻辑）
                if (!TryCreateValidImageUri(url, out var uri))
                    return;

                // 流式下载：先读 headers 校验 Content-Type 和 Content-Length
                using var response = await SharedHttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                // 校验 Content-Type
                var contentType = response.Content.Headers.ContentType?.MediaType;
                if (contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
                    return;

                // 校验 Content-Length
                if (response.Content.Headers.ContentLength > MaxImageBytes)
                    return;

                // 流式读取到临时文件，累计字节数并强制上限
                var localPath = GetLocalPath(animeId);
                var tempPath = localPath + ".tmp";
                try
                {
                    using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    var buffer = new byte[81920];
                    long totalRead = 0;
                    int bytesRead;
                    while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
                    {
                        totalRead += bytesRead;
                        if (totalRead > MaxImageBytes)
                        {
                            // 超过上限，中止并清理
                            fileStream.Close();
                            TryDelete(tempPath);
                            return;
                        }
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                    }
                }
                catch
                {
                    TryDelete(tempPath);
                    throw;
                }

                // 原子重命名
                if (File.Exists(localPath))
                    TryDelete(localPath);
                File.Move(tempPath, localPath);

                _cachedIds.TryAdd(animeId, 0);

                // 触发淘汰检查
                _ = EvictIfNeededAsync();
            }
            catch (HttpRequestException)
            {
                // 下载失败时不抛异常，下次会自动重试
            }
            catch (TaskCanceledException)
            {
                // 下载超时或取消时不抛异常
            }
            catch (IOException)
            {
                // 文件写入失败时不抛异常
            }
            finally
            {
                DownloadThrottle.Release();
            }
        }

        /// <summary>清除所有缓存的图片文件。</summary>
        public static void ClearAll()
        {
            _cachedIds.Clear();
            if (!Directory.Exists(CacheDir)) return;
            foreach (var file in Directory.GetFiles(CacheDir, "*.jpg"))
            {
                TryDelete(file);
            }
        }

        /// <summary>获取图片缓存统计信息。</summary>
        public static (int count, double sizeMB) GetCacheStats()
        {
            if (!Directory.Exists(CacheDir))
                return (0, 0);

            var files = Directory.GetFiles(CacheDir, "*.jpg");
            long totalBytes = 0;
            foreach (var file in files)
            {
                try { totalBytes += new FileInfo(file).Length; } catch (IOException) { }
            }
            return (files.Length, totalBytes / 1024.0 / 1024.0);
        }

        // ---- 私有辅助 ----

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
        }

        /// <summary>
        /// 检查当前图片缓存总大小，超过 MaxCacheSizeMB 时淘汰最旧文件。
        /// 使用独立锁防止并发淘汰。
        /// </summary>
        private static async Task EvictIfNeededAsync()
        {
            if (!await _evictionLock.WaitAsync(0))
                return;

            try
            {
                var files = Directory.GetFiles(CacheDir, "*.jpg");
                long totalBytes = 0;
                var fileInfos = new List<(FileInfo info, string path)>(files.Length);

                foreach (var f in files)
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        totalBytes += fi.Length;
                        fileInfos.Add((fi, f));
                    }
                    catch (IOException) { }
                }

                var maxBytes = MaxCacheSizeMB * 1024L * 1024L;
                if (totalBytes <= maxBytes)
                    return;

                fileInfos.Sort((a, b) => a.info.LastWriteTimeUtc.CompareTo(b.info.LastWriteTimeUtc));

                var targetBytes = (long)(maxBytes * 0.8);
                foreach (var (info, path) in fileInfos)
                {
                    if (totalBytes <= targetBytes)
                        break;

                    try
                    {
                        var idStr = Path.GetFileNameWithoutExtension(path);
                        File.Delete(path);
                        totalBytes -= info.Length;
                        if (int.TryParse(idStr, out var id))
                            _cachedIds.TryRemove(id, out _);
                    }
                    catch (IOException) { }
                }
            }
            finally
            {
                _evictionLock.Release();
            }
        }
    }
}
