using System.Diagnostics;
using AniMeido.Contracts.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using Windows.Foundation;

namespace AniMeido.Plugin.Base.Services;

internal static class AnimeCardDragTokenVisualFactory
{
    private const int TokenSize = 72;
    private sealed record CacheKey(string Path, DateTime LastWriteTimeUtc);
    private static readonly Dictionary<CacheKey, byte[]> Cache = new();
    private static readonly object CacheLock = new();

    public static void TryApplyDragToken(DragStartingEventArgs args, Anime anime)
        => _ = ApplyTokenAsync(args, anime, args.GetDeferral());

    private static async Task ApplyTokenAsync(DragStartingEventArgs args, Anime anime,
        DragOperationDeferral deferral)
    {
        try
        {
            var path = ImageCacheHelper.HasLocalCache(anime.ID)
                ? ImageCacheHelper.GetLocalPath(anime.ID)
                : ImageCacheHelper.PlaceholderPath;
            var png = await Task.Run(() => CreateToken(path));
            using var stream = new MemoryStream(png);
            var image = new BitmapImage();
            await image.SetSourceAsync(stream.AsRandomAccessStream());
            args.DragUI.SetContentFromBitmapImage(image, new Point(TokenSize / 2d, TokenSize / 2d));
        }
#pragma warning disable CA1031 // 拖拽视觉失败仍保留已建立的数据传输。
        catch (Exception ex)
        {
            Debug.WriteLine($"[DragToken] Desktop token failed: {ex}");
        }
#pragma warning restore CA1031
        finally
        {
            deferral.Complete();
        }
    }

    private static byte[] CreateToken(string path)
    {
        var key = new CacheKey(path, File.GetLastWriteTimeUtc(path));
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var cached))
                return cached;
        }

        using var surface = SKSurface.Create(new SKImageInfo(TokenSize, TokenSize,
            SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        using var circleBuilder = new SKPathBuilder();
        circleBuilder.AddCircle(TokenSize / 2f, TokenSize / 2f, TokenSize / 2f - 1);
        using var circle = circleBuilder.Detach();
        canvas.ClipPath(circle, antialias: true);
        using var source = File.Exists(path) ? SKImage.FromEncodedData(path) : null;
        if (source is not null)
        {
            var side = Math.Min(source.Width, source.Height);
            var sourceRect = SKRect.Create((source.Width - side) / 2f,
                (source.Height - side) / 2f, side, side);
            canvas.DrawImage(source, sourceRect, SKRect.Create(TokenSize, TokenSize),
                new SKSamplingOptions(SKFilterMode.Linear));
        }
        else
        {
            using var paint = new SKPaint { Color = new SKColor(0, 102, 255) };
            canvas.DrawRect(SKRect.Create(TokenSize, TokenSize), paint);
        }
        using var snapshot = surface.Snapshot();
        using var encoded = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        var result = encoded.ToArray();
        lock (CacheLock)
            Cache[key] = result;
        return result;
    }
}
