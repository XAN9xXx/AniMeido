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

        public ImageState(Image image)
        {
            _image = image;
            _image.Loaded += OnLoaded;
            _image.Unloaded += OnUnloaded;
            _image.ImageFailed += OnImageFailed;
            _image.SizeChanged += OnSizeChanged;
        }

        public void Configure(ImageRequest request)
        {
            if (_request == request)
                return;

            CancelCurrent();
            _request = request;
            _decodeRetryUsed = false;
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
                _image.Source = null;
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e) => Start();

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            CancelCurrent();
            _showingManagedImage = false;
            _image.Source = null;
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
            _image.Source = new BitmapImage
            {
                DecodePixelWidth = _decodePixelWidth,
                UriSource = new Uri(path),
            };
        }

        private void ShowPlaceholder()
        {
            _showingManagedImage = false;
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
