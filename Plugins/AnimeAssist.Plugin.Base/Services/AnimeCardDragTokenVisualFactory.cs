using AniMeido.Contracts.Models;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using System.Diagnostics;

namespace AniMeido.Plugin.Base.Services
{
    /// <summary>
    /// 为 AnimeCard 标准拖拽生成 72×72 圆形封面 DragToken 视觉。
    /// 使用 SoftwareBitmap 居中裁剪 + 圆形 alpha mask（含边缘抗锯齿）。
    /// Fallback 链：本地缓存封面 → 本地 placeholder → 蓝色诊断圆 → 系统默认。
    /// 含简单内存缓存，重复拖拽同一封面不重复解码。
    /// </summary>
    internal static class AnimeCardDragTokenVisualFactory
    {
        private const int TokenSize = 72;
        private const double TokenAnchorRatio = 0.5;
        private const int SmoothEdgePixels = 2;

        // ======== 简单内存缓存 ========
        private sealed record TokenCacheKey(string Path, DateTime LastWriteTimeUtc, int Size);
        private static readonly Dictionary<TokenCacheKey, byte[]> TokenPixelCache = new();
        private static readonly object CacheLock = new();

        /// <summary>尝试为拖拽设置圆形封面 DragToken 视觉。</summary>
        public static void TryApplyDragToken(Microsoft.UI.Xaml.DragStartingEventArgs args, Anime anime)
        {
            Debug.WriteLine($"[DragToken] TryApplyDragToken ENTER: animeId={anime.ID}, title={anime.Title}");

            var deferral = args.GetDeferral();
            _ = TryApplyCircularTokenAsync(args, anime, deferral);
        }

        private static async Task TryApplyCircularTokenAsync(
            Microsoft.UI.Xaml.DragStartingEventArgs args, Anime anime, Microsoft.UI.Xaml.DragOperationDeferral deferral)
        {
            try
            {
                // ========== Phase 1: 本地缓存封面 ==========
                if (ImageCacheHelper.HasLocalCache(anime.ID))
                {
                    string localPath = ImageCacheHelper.GetLocalPath(anime.ID);
                    if (File.Exists(localPath))
                    {
                        var decoded = await GetOrDecodeAsync(localPath);
                        if (decoded != null)
                        {
                            var masked = ApplyCircularMask(decoded);
                            await ApplySoftwareBitmapAsync(args, masked);
                            Debug.WriteLine($"[DragToken] Circular cover token applied");
                            return;
                        }
                    }
                }

                // ========== Phase 2: 本地 placeholder ==========
                string placeholderPath = ImageCacheHelper.PlaceholderPath;
                if (File.Exists(placeholderPath))
                {
                    var decoded = await GetOrDecodeAsync(placeholderPath);
                    if (decoded != null)
                    {
                        var masked = ApplyCircularMask(decoded);
                        await ApplySoftwareBitmapAsync(args, masked);
                        Debug.WriteLine($"[DragToken] Circular placeholder token applied");
                        return;
                    }
                }

                // ========== Phase 3: 蓝色诊断圆 fallback ==========
                Debug.WriteLine($"[DragToken] Using blue diagnostic circle as last resort");
                var fallback = GenerateFallbackCirclePixels();
                await ApplySoftwareBitmapAsync(args, fallback);
            }
#pragma warning disable CA1031
            catch (Exception ex)
            {
                Debug.WriteLine($"[DragToken] TryApplyCircularTokenAsync EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                // 不 throw，不 args.Cancel，让系统默认 DragUI 接管
            }
#pragma warning restore CA1031
            finally
            {
                deferral.Complete();
            }
        }

        /// <summary>带缓存的解码：优先查缓存，未命中则解码后写入缓存。</summary>
        private static async Task<byte[]?> GetOrDecodeAsync(string localPath)
        {
            var fileInfo = new FileInfo(localPath);
            var key = new TokenCacheKey(localPath, fileInfo.LastWriteTimeUtc, TokenSize);

            lock (CacheLock)
            {
                if (TokenPixelCache.TryGetValue(key, out var cached))
                {
                    return cached;
                }
            }

            var decoded = await TryDecodeLocalFileAsync(localPath);
            if (decoded != null)
            {
                lock (CacheLock)
                {
                    TokenPixelCache[key] = decoded;
                }
            }
            return decoded;
        }

        /// <summary>尝试从本地文件解码为 72×72 居中裁剪的 BGRA8 像素数据。</summary>
        private static async Task<byte[]?> TryDecodeLocalFileAsync(string localPath)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(localPath);
                using var fileStream = await file.OpenAsync(FileAccessMode.Read);
                var decoder = await BitmapDecoder.CreateAsync(fileStream);

                int srcW = (int)decoder.PixelWidth;
                int srcH = (int)decoder.PixelHeight;

                // 解码全尺寸
                var fullData = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    new BitmapTransform(),
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.ColorManageToSRgb);
                var fullPixels = fullData.DetachPixelData();
                int srcStride = (srcW * 4 + 3) / 4 * 4;

                // 手动居中裁剪 + 缩放到 TokenSize
                int cropSize = Math.Min(srcW, srcH);
                int cropX = (srcW - cropSize) / 2;
                int cropY = (srcH - cropSize) / 2;
                var output = new byte[TokenSize * TokenSize * 4];

                for (int y = 0; y < TokenSize; y++)
                {
                    for (int x = 0; x < TokenSize; x++)
                    {
                        int srcX = cropX + (x * cropSize / TokenSize);
                        int srcY = cropY + (y * cropSize / TokenSize);
                        int srcIdx = (int)(srcY * srcStride + srcX * 4);
                        int outIdx = (y * TokenSize + x) * 4;

                        output[outIdx] = fullPixels[srcIdx];     // B
                        output[outIdx + 1] = fullPixels[srcIdx + 1]; // G
                        output[outIdx + 2] = fullPixels[srcIdx + 2]; // R
                        output[outIdx + 3] = fullPixels[srcIdx + 3]; // A
                    }
                }

                return output;
            }
#pragma warning disable CA1031
            catch (Exception ex)
            {
                Debug.WriteLine($"[DragToken] Decode FAILED: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
#pragma warning restore CA1031
        }

        /// <summary>
        /// 对 72×72 BGRA8 像素数据应用圆形 alpha mask，含边缘抗锯齿过渡。
        /// 圆边缘 SmoothEdgePixels 像素范围内 alpha 从 255 渐变到 0。
        /// </summary>
        private static byte[] ApplyCircularMask(byte[] bgraPixels)
        {
            int center = TokenSize / 2;
            int radius = center - 1;               // 保留 1px 内缩避免边缘锯齿
            int fadeStart = radius - SmoothEdgePixels;
            var output = new byte[bgraPixels.Length];

            for (int y = 0; y < TokenSize; y++)
            {
                for (int x = 0; x < TokenSize; x++)
                {
                    int dx = x - center;
                    int dy = y - center;
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    int idx = (y * TokenSize + x) * 4;

                    if (dist <= fadeStart)
                    {
                        // 完全在圆内：不透明
                        output[idx] = bgraPixels[idx];
                        output[idx + 1] = bgraPixels[idx + 1];
                        output[idx + 2] = bgraPixels[idx + 2];
                        output[idx + 3] = 255;
                    }
                    else if (dist <= radius)
                    {
                        // 边缘过渡区：alpha 线性渐变，RGB 做 premultiply
                        double t = (dist - fadeStart) / SmoothEdgePixels;
                        int alpha = (int)(255 * (1.0 - t));
                        alpha = Math.Clamp(alpha, 0, 255);
                        double a = alpha / 255.0;
                        output[idx] = (byte)(bgraPixels[idx] * a);
                        output[idx + 1] = (byte)(bgraPixels[idx + 1] * a);
                        output[idx + 2] = (byte)(bgraPixels[idx + 2] * a);
                        output[idx + 3] = (byte)alpha;
                    }
                    // else: 圆外透明
                }
            }
            return output;
        }

        /// <summary>将 BGRA8 像素数据编码为 PNG → SoftwareBitmap → 设置到 DragUI。</summary>
        private static async Task ApplySoftwareBitmapAsync(Microsoft.UI.Xaml.DragStartingEventArgs args, byte[] bgraPixels)
        {
            var memStream = new InMemoryRandomAccessStream();

            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memStream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                (uint)TokenSize, (uint)TokenSize,
                96, 96,
                bgraPixels);
            await encoder.FlushAsync();
            memStream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(memStream);
            var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

            var anchor = new Point(TokenSize * TokenAnchorRatio, TokenSize * TokenAnchorRatio);
            args.DragUI.SetContentFromSoftwareBitmap(softwareBitmap, anchor);

            Debug.WriteLine($"[DragToken] SetContentFromSoftwareBitmap OK");
        }

        /// <summary>生成蓝色诊断圆形像素数据（最后兜底）。</summary>
        private static byte[] GenerateFallbackCirclePixels()
        {
            int center = TokenSize / 2;
            int radius = center - 1;
            int fadeStart = radius - SmoothEdgePixels;
            var pixels = new byte[TokenSize * TokenSize * 4];

            for (int y = 0; y < TokenSize; y++)
            {
                for (int x = 0; x < TokenSize; x++)
                {
                    int dx = x - center;
                    int dy = y - center;
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    int idx = (y * TokenSize + x) * 4;

                    if (dist <= fadeStart)
                    {
                        pixels[idx] = 0xFF; // B
                        pixels[idx + 1] = 0x66; // G
                        pixels[idx + 2] = 0x00; // R
                        pixels[idx + 3] = 0xFF; // A
                    }
                    else if (dist <= radius)
                    {
                        double t = (dist - fadeStart) / SmoothEdgePixels;
                        int alpha = (int)(255 * (1.0 - t));
                        alpha = Math.Clamp(alpha, 0, 255);
                        double a = alpha / 255.0;
                        pixels[idx] = (byte)(0xFF * a);
                        pixels[idx + 1] = (byte)(0x66 * a);
                        pixels[idx + 2] = (byte)(0x00 * a);
                        pixels[idx + 3] = (byte)alpha;
                    }
                    // else: transparent
                }
            }
            return pixels;
        }
    }
}
