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
    private BitmapImage? _loadingImage;
    private TaskCompletionSource? _imageLoad;

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

        if (_loadingImage is { } previousImage)
        {
            previousImage.ImageOpened -= OnImageLoadCompleted;
            previousImage.ImageFailed -= OnImageLoadCompleted;
            _imageLoad?.TrySetResult();
        }

        _loadingImage = new BitmapImage();
        _imageLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _loadingImage.ImageOpened += OnImageLoadCompleted;
        _loadingImage.ImageFailed += OnImageLoadCompleted;
        _loadingImage.UriSource = new Uri($"file:///{splashPath.Replace('\\', '/')}");
        _splashImage.Source = _loadingImage;
    }

    private void OnImageLoadCompleted(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _loadingImage))
            _imageLoad?.TrySetResult();
    }

    public async Task WaitForImageAsync()
    {
        var image = _loadingImage;
        var imageLoad = _imageLoad?.Task;
        if (image is null || imageLoad is null)
            return;

        try
        {
            await Task.WhenAny(imageLoad, Task.Delay(5000));
        }
        finally
        {
            image.ImageOpened -= OnImageLoadCompleted;
            image.ImageFailed -= OnImageLoadCompleted;
        }
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
