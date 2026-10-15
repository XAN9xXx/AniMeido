using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AniMeido.App.Services;

public sealed class SplashCoordinator
{
    private readonly Image _splashImage;
    private readonly UIElement _splashOverlay;

    public SplashCoordinator(Image splashImage, UIElement splashOverlay)
    {
        _splashImage = splashImage;
        _splashOverlay = splashOverlay;
    }

    /// <summary>
    /// 加载开屏图（非打包模式下用本地路径）。
    /// </summary>
    public void LoadSplashImage()
    {
        var splashPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "SplashScreen.png");
        if (!System.IO.File.Exists(splashPath)) return;

        var img = new BitmapImage();
        img.UriSource = new Uri($"file:///{splashPath.Replace('\\', '/')}");
        _splashImage.Source = img;
    }

    public async Task WaitForImageAsync()
    {
        if (_splashImage.Source is not BitmapImage bmp) return;

        var tcs = new TaskCompletionSource();
        bmp.ImageOpened += (_, _) => tcs.TrySetResult();
        bmp.ImageFailed += (_, _) => tcs.TrySetResult();
        await Task.WhenAny(tcs.Task, Task.Delay(5000));
    }

    public async Task FadeOutAsync()
    {
        await Task.Delay(800);

        var visual = ElementCompositionPreview.GetElementVisual(_splashOverlay);
        var compositor = visual.Compositor;

        var fadeOut = compositor.CreateScalarKeyFrameAnimation();
        fadeOut.InsertKeyFrame(0.0f, 1.0f);
        fadeOut.InsertKeyFrame(1.0f, 0.0f);
        fadeOut.Duration = TimeSpan.FromMilliseconds(600);
        visual.StartAnimation("Opacity", fadeOut);

        await Task.Delay(800);
        _splashOverlay.Visibility = Visibility.Collapsed;
    }
}
