using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using SkiaSharp;
using Xunit;

namespace AniMeido.Desktop.Tests;

internal static class UnoVisualEvidence
{
    internal static async Task WaitForAsync(Func<bool> done)
    {
        for (var i = 0; i < 150 && !done(); ++i) await Task.Delay(16);
        Assert.True(done());
    }

    internal static IEnumerable<DependencyObject> Descendants(DependencyObject element)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            yield return child;
            foreach (var next in Descendants(child)) yield return next;
        }
    }

    internal static async Task CaptureAsync(FrameworkElement element, string batch, string name)
    {
        if (Environment.GetEnvironmentVariable("ANIMEIDO_FINAL_VISUAL_DIR") is not { Length: > 0 } root) return;
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        Assert.True(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0);
        Assert.Contains(pixels, value => value != 0);
        var directory = Directory.CreateDirectory(Path.Combine(root, batch));
        using var image = SKImage.FromPixelCopy(new SKImageInfo(bitmap.PixelWidth, bitmap.PixelHeight,
            SKColorType.Bgra8888, SKAlphaType.Premul), pixels);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(Path.Combine(directory.FullName, $"Skia-{name}.png"));
        png.SaveTo(file);
    }

    internal static void SetScale(XamlRoot root, float scale)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var tree = typeof(XamlRoot).GetProperty("VisualTree", flags)!.GetValue(root)!;
        var engine = tree.GetType().GetProperty("RootScale", flags)!.GetValue(tree)!;
        engine.GetType().GetMethod("SetTestOverride", flags)!.Invoke(engine, [scale]);
        typeof(XamlRoot).GetMethod("RaiseChangedEvent", flags)!.Invoke(root, null);
    }
}
