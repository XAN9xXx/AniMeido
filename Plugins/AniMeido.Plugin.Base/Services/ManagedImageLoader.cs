using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AniMeido.Plugin.Base.Services;

internal enum ManagedImageLoadState
{
    Loading,
    Loaded,
    Failed,
}

/// <summary>
/// Binds image cache requests to a WinUI Image lifetime without retaining
/// recycled or unloaded controls.
/// </summary>
internal static class ManagedImageLoader
{
    private static readonly ConditionalWeakTable<Image, ImageState> States = new();

    public static void ConfigureCover(
        Image image,
        int animeId,
        string? url,
        double logicalWidth,
        Action<ManagedImageLoadState>? stateChanged = null)
        => GetState(image).Configure(new ImageRequest(
            ImageRequestKind.Cover,
            animeId,
            url,
            logicalWidth,
            stateChanged));

    public static void ConfigureAvatar(
        Image image,
        string? url,
        double logicalWidth)
        => GetState(image).Configure(new ImageRequest(
            ImageRequestKind.Avatar,
            0,
            url,
            logicalWidth,
            null));

    public static void ConfigureLocal(
        Image image,
        string? filePath,
        double logicalWidth)
        => GetState(image).Configure(new ImageRequest(
            ImageRequestKind.Local,
            0,
            filePath,
            logicalWidth,
            null));

    public static void Cancel(Image image, bool clearSource = true)
    {
        if (States.TryGetValue(image, out var state))
            state.Cancel(clearSource);
    }

    public static void Retry(Image image)
    {
        if (States.TryGetValue(image, out var state))
            state.Retry();
    }

    internal static int CalculateDecodePixelWidth(
        double logicalWidth,
        double actualWidth,
        double rasterizationScale)
    {
        var width = actualWidth > 0 ? actualWidth : logicalWidth;
        var scale = rasterizationScale > 0 ? rasterizationScale : 1;
        return Math.Max(1, (int)Math.Ceiling(width * scale));
    }

    private static ImageState GetState(Image image)
        => States.GetValue(image, static value => new ImageState(value));

    private enum ImageRequestKind
    {
        Cover,
        Avatar,
        Local,
    }

    private sealed record ImageRequest(
        ImageRequestKind Kind,
        int AnimeId,
        string? Source,
        double LogicalWidth,
        Action<ManagedImageLoadState>? StateChanged);

    private sealed class ImageState
    {
        private readonly Image _image;
        private CancellationTokenSource? _cancellation;
        private ImageRequest? _request;
        private int _version;
        private int _decodePixelWidth;
        private bool _showingManagedImage;
        private bool _decodeRetryUsed;
        private BitmapImage? _managedBitmap;
        private bool _imageOpened;

        public ImageState(Image image)
        {
            _image = image;
            _image.Loaded += OnLoaded;
            _image.Unloaded += OnUnloaded;
            _image.ImageFailed += OnImageFailed;
            _image.ImageOpened += OnImageOpened;
            _image.SizeChanged += OnSizeChanged;
        }

        public void Configure(ImageRequest request)
        {
            if (_request == request)
                return;

            CancelCurrent();
            _request = request;
            _decodeRetryUsed = false;
            // 已有磁盘缓存时直接挂到 Image，让 WinUI 加载；不先切一遍占位图。
            // 换作品仍立即替换 Source，不把上一部作品的封面留到异步请求完成。
            if (_image.IsLoaded && GetExistingLocalPath(request) is { } path)
            {
                ShowLocalImage(path, request);
                Report(request, ManagedImageLoadState.Loaded);
                return;
            }
            ShowPlaceholder();
            if (_image.IsLoaded)
                Start();
        }

        public void Cancel(bool clearSource)
        {
            CancelCurrent();
            _request = null;
            if (clearSource)
            {
                _showingManagedImage = false;
                _managedBitmap = null;
                _imageOpened = false;
                _image.Source = null;
            }
        }

        public void Retry()
        {
            if (_request is null || !_image.IsLoaded)
                return;

            if (_request.Kind == ImageRequestKind.Cover)
                ImageCacheHelper.InvalidateCover(_request.AnimeId);
            else if (_request.Kind == ImageRequestKind.Avatar
                && !string.IsNullOrWhiteSpace(_request.Source))
            {
                ImageCacheHelper.InvalidateAvatar(_request.Source);
            }

            _decodeRetryUsed = false;
            ShowPlaceholder();
            Start();
        }

        private void OnLoaded(object sender, RoutedEventArgs e) => Start();

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            // 跨浮层迁移可能交错触发事件；不取消已经重新进入可视树的请求。
            if (_image.IsLoaded) return;
            CancelCurrent();
            // 仅保留这个控件已成功显示的同一图片，不新建缓存，也不保留失败/在途图片。
            if (_imageOpened && _showingManagedImage && ReferenceEquals(_image.Source, _managedBitmap))
                return;
            _showingManagedImage = false;
            _managedBitmap = null;
            _imageOpened = false;
            _image.Source = null;
        }

        private void OnImageOpened(object sender, RoutedEventArgs e)
        {
            // 图片始终先赋给 Image.Source；此事件只记录能否复用，不作为显示的前置条件。
            if (_showingManagedImage && _managedBitmap is { PixelWidth: > 0, PixelHeight: > 0 }
                && ReferenceEquals(_image.Source, _managedBitmap))
                _imageOpened = true;
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_image.IsLoaded || !_showingManagedImage || _request is null)
                return;

            var desired = GetDecodePixelWidth(_request);
            if (Math.Abs(desired - _decodePixelWidth) < 32)
                return;

            var path = GetExistingLocalPath(_request);
            if (path is not null)
                ShowLocalImage(path, _request);
        }

        private void Start()
        {
            if (_request is null || !_image.IsLoaded)
                return;

            if (_showingManagedImage && _managedBitmap is not null
                && ReferenceEquals(_image.Source, _managedBitmap)
                && Math.Abs(GetDecodePixelWidth(_request) - _decodePixelWidth) < 32)
            {
                Report(_request, ManagedImageLoadState.Loaded);
                return;
            }
            CancelCurrent();
            _cancellation = new CancellationTokenSource();
            var version = ++_version;
            _ = LoadAsync(_request, version, _cancellation.Token);
        }

        private async Task LoadAsync(
            ImageRequest request,
            int version,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request.Source))
            {
                Report(request, ManagedImageLoadState.Failed);
                return;
            }

            Report(request, ManagedImageLoadState.Loading);
            string? localPath = GetExistingLocalPath(request);
            if (localPath is null && request.Kind != ImageRequestKind.Local)
            {
                var succeeded = request.Kind == ImageRequestKind.Cover
                    ? await ImageCacheHelper.CacheImageAsync(
                        request.AnimeId,
                        request.Source,
                        cancellationToken)
                    : await ImageCacheHelper.CacheAvatarAsync(
                        request.Source,
                        cancellationToken);
                if (succeeded)
                    localPath = GetExistingLocalPath(request);
            }

            if (cancellationToken.IsCancellationRequested
                || version != _version
                || !_image.IsLoaded
                || !ReferenceEquals(request, _request))
            {
                return;
            }

            if (localPath is null || !File.Exists(localPath))
            {
                ShowPlaceholder();
                Report(request, ManagedImageLoadState.Failed);
                return;
            }

            ShowLocalImage(localPath, request);
            Report(request, ManagedImageLoadState.Loaded);
        }

        private void OnImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            if (!_showingManagedImage || _request is null)
                return;

            _showingManagedImage = false;
            if (!_decodeRetryUsed && _request.Kind != ImageRequestKind.Local)
            {
                _decodeRetryUsed = true;
                if (_request.Kind == ImageRequestKind.Cover)
                    ImageCacheHelper.InvalidateCover(_request.AnimeId);
                else
                    ImageCacheHelper.InvalidateAvatar(_request.Source!);
                ShowPlaceholder();
                Start();
                return;
            }

            ShowPlaceholder();
            Report(_request, ManagedImageLoadState.Failed);
        }

        private void ShowLocalImage(string path, ImageRequest request)
        {
            _decodePixelWidth = GetDecodePixelWidth(request);
            _showingManagedImage = true;
            _imageOpened = false;
            _managedBitmap = new BitmapImage
            {
                DecodePixelWidth = _decodePixelWidth,
                UriSource = new Uri(path),
            };
            _image.Source = _managedBitmap;
        }

        private void ShowPlaceholder()
        {
            _showingManagedImage = false;
            _managedBitmap = null;
            _imageOpened = false;
            _image.Source = new BitmapImage(ImageCacheHelper.PlaceholderUri);
        }

        private string? GetExistingLocalPath(ImageRequest request)
        {
            var path = request.Kind switch
            {
                ImageRequestKind.Cover when ImageCacheHelper.HasLocalCache(request.AnimeId)
                    => ImageCacheHelper.GetLocalPath(request.AnimeId),
                ImageRequestKind.Avatar when ImageCacheHelper.HasAvatarCache(request.Source!)
                    => ImageCacheHelper.GetAvatarLocalPath(request.Source!),
                ImageRequestKind.Local => request.Source,
                _ => null,
            };
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
        }

        private int GetDecodePixelWidth(ImageRequest request)
            => CalculateDecodePixelWidth(
                request.LogicalWidth,
                _image.ActualWidth,
                _image.XamlRoot?.RasterizationScale ?? 1);

        private static void Report(
            ImageRequest request,
            ManagedImageLoadState state)
            => request.StateChanged?.Invoke(state);

        private void CancelCurrent()
        {
            _version++;
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }
}
