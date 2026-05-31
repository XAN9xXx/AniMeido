using System.Collections.Generic;
using System.Threading;

namespace AniMeido.Plugin.Base.Services
{
    /// <summary>
    /// 番剧封面图本地缓存辅助类。
    /// 图片保存在 {AppContext.BaseDirectory}/imagecache/{animeId}.jpg。
    /// 当总大小超过 MaxCacheSizeMB 时，自动淘汰最旧的文件。
    /// </summary>
    internal static class ImageCacheHelper
    {
        private static readonly string CacheDir;
        private static readonly SemaphoreSlim DownloadThrottle = new(4, 4);
        private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly HashSet<int> _cachedIds = new();
        private static readonly SemaphoreSlim _evictionLock = new(1, 1);

        /// <summary>
        /// 图片缓存大小上限（MB）。超过时淘汰最旧文件。
        /// </summary>
        public const int MaxCacheSizeMB = 500;

        /// <summary>
        /// 占位图 URI
        /// </summary>
        public static readonly Uri PlaceholderUri;

        static ImageCacheHelper()
        {
            CacheDir = Path.Combine(AppContext.BaseDirectory, "imagecache");
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
                        _cachedIds.Add(id);
                }
            }
        }

        /// <summary>
        /// 获取本地缓存路径（不保证文件存在）。
        /// </summary>
        public static string GetLocalPath(int animeId)
            => Path.Combine(CacheDir, $"{animeId}.jpg");

        /// <summary>
        /// 检查本地是否有已缓存的图片。
        /// </summary>
        public static bool HasLocalCache(int animeId)
            => _cachedIds.Contains(animeId);

        /// <summary>
        /// 获取用于 Image.Source 的 URI。
        /// 本地缓存存在则返回 file:/// 路径，否则返回原始 URL。
        /// </summary>
        public static Uri GetImageUri(int animeId, string? originalUrl)
        {
            if (_cachedIds.Contains(animeId))
                return new Uri(GetLocalPath(animeId));

            if (!string.IsNullOrEmpty(originalUrl))
                return new Uri(originalUrl);

            return PlaceholderUri;
        }

        /// <summary>
        /// 从网络 URL 下载图片并保存到本地缓存。
        /// 如果文件已存在则跳过。下载后检查总大小，超过上限则触发淘汰。
        /// </summary>
        public static async Task CacheImageAsync(int animeId, string url)
        {
            if (_cachedIds.Contains(animeId)) return;

            await DownloadThrottle.WaitAsync();
            try
            {
                if (_cachedIds.Contains(animeId)) return;

                var bytes = await SharedHttpClient.GetByteArrayAsync(url).ConfigureAwait(false);
                var localPath = GetLocalPath(animeId);
                await File.WriteAllBytesAsync(localPath, bytes).ConfigureAwait(false);
                _cachedIds.Add(animeId);

                // 触发淘汰检查（不等待完成，不阻塞下载流程）
                _ = EvictIfNeededAsync();
            }
            catch
            {
                // 下载失败时不抛异常，下次会自动重试
            }
            finally
            {
                DownloadThrottle.Release();
            }
        }

        /// <summary>
        /// 清除所有缓存的图片文件。
        /// </summary>
        public static void ClearAll()
        {
            _cachedIds.Clear();
            if (!Directory.Exists(CacheDir)) return;
            foreach (var file in Directory.GetFiles(CacheDir, "*.jpg"))
            {
                try { File.Delete(file); } catch { }
            }
        }

        /// <summary>
        /// 获取图片缓存统计信息。
        /// </summary>
        public static (int count, double sizeMB) GetCacheStats()
        {
            if (!Directory.Exists(CacheDir))
                return (0, 0);

            var files = Directory.GetFiles(CacheDir, "*.jpg");
            long totalBytes = 0;
            foreach (var file in files)
            {
                try { totalBytes += new FileInfo(file).Length; } catch { }
            }
            return (files.Length, totalBytes / 1024.0 / 1024.0);
        }

        // ---- 私有辅助 ----

        /// <summary>
        /// 检查当前图片缓存总大小，超过 MaxCacheSizeMB 时淘汰最旧文件。
        /// 使用独立锁防止并发淘汰。
        /// </summary>
        private static async Task EvictIfNeededAsync()
        {
            if (!await _evictionLock.WaitAsync(0))
                return; // 已有淘汰在进行中

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
                    catch { }
                }

                var maxBytes = MaxCacheSizeMB * 1024L * 1024L;
                if (totalBytes <= maxBytes)
                    return;

                // 按最后写入时间升序排序（最旧在前）
                fileInfos.Sort((a, b) => a.info.LastWriteTimeUtc.CompareTo(b.info.LastWriteTimeUtc));

                // 删除最旧的直到低于上限的 80%（避免频繁淘汰）
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
                            _cachedIds.Remove(id);
                    }
                    catch { }
                }
            }
            finally
            {
                _evictionLock.Release();
            }
        }
    }
}
