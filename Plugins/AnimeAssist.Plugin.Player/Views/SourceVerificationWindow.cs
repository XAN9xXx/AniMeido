using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using AniMeido.Plugin.Player.Sources.Web;
using System.Text.Json;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace AniMeido.Plugin.Player.Views;

internal sealed class SourceVerificationWindow : Window
{
    private readonly CoreWebView2Environment _environment;
    private readonly Uri _uri;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly string? _cookies;
    private readonly Func<CancellationToken, Task<bool>> _verifyAsync;
    private readonly DispatcherQueue _dispatcher;
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Grid _root = new();
    private readonly Border _browserPlaceholder = new();
    private readonly TextBlock _address = new();
    private readonly TextBlock _status = new();
    private readonly Button _retryButton = new()
    {
        Content = "检查并继续",
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _webView;
    private XamlRoot? _trackedXamlRoot;
    private bool _checking;
    private bool _closed;

    public SourceVerificationWindow(
        CoreWebView2Environment environment,
        Uri uri,
        IReadOnlyDictionary<string, string> headers,
        string? cookies,
        Func<CancellationToken, Task<bool>> verifyAsync)
    {
        _environment = environment;
        _uri = uri;
        _headers = headers;
        _cookies = cookies;
        _verifyAsync = verifyAsync;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Title = "AniMeido 源站验证";
        Content = BuildLayout();
        ResizeWindow();
        _root.SizeChanged += OnRootSizeChanged;
        Closed += OnClosed;
    }

    public async Task<bool> ShowAsync(CancellationToken cancellationToken)
    {
        Activate();
        TrackDpiChanges();
        try
        {
            await InitializeBrowserAsync();
            using var registration = cancellationToken.Register(CancelAndClose);
            return await _completion.Task.WaitAsync(cancellationToken);
        }
        catch
        {
            CancelAndClose();
            throw;
        }
    }

    public void CancelAndClose()
    {
        if (_dispatcher.HasThreadAccess)
        {
            CloseWindow();
            return;
        }

        _ = _dispatcher.TryEnqueue(CloseWindow);
    }

    private UIElement BuildLayout()
    {
        _root.Background = PlayerVisualStyles.WindowBackground;
        _root.RowDefinitions.Add(
            new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition());

        var header = new Grid
        {
            Padding = new Thickness(14, 10, 14, 10),
            ColumnSpacing = 10,
            Background = PlayerVisualStyles.SurfaceBackground,
        };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Auto });

        var information = new StackPanel { Spacing = 2 };
        information.Children.Add(
            PlayerVisualStyles.CreatePageTitle("源站验证"));
        _address.Text = _uri.Host;
        _address.Opacity = 0.7;
        _address.TextTrimming = TextTrimming.CharacterEllipsis;
        information.Children.Add(_address);
        _status.Text =
            "请在下方完成登录或人机验证。AniMeido 不会读取或单独保存密码。";
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Opacity = 0.8;
        information.Children.Add(_status);
        header.Children.Add(information);

        _retryButton.Click += OnCompleteClick;
        PlayerVisualStyles.StyleButton(
            _retryButton,
            PlayerButtonTone.Primary);
        Grid.SetColumn(_retryButton, 1);
        header.Children.Add(_retryButton);

        var cancel = new Button
        {
            Content = "取消",
            VerticalAlignment = VerticalAlignment.Center,
        };
        PlayerVisualStyles.StyleButton(cancel);
        cancel.Click += OnCancelClick;
        Grid.SetColumn(cancel, 2);
        header.Children.Add(cancel);
        _root.Children.Add(header);

        _browserPlaceholder.Background = new SolidColorBrush(Colors.White);
        Grid.SetRow(_browserPlaceholder, 1);
        _root.Children.Add(_browserPlaceholder);
        return _root;
    }

    private async Task InitializeBrowserAsync()
    {
        await WaitForLayoutAsync();
        if (_closed)
        {
            return;
        }

        var windowHandle = WindowNative.GetWindowHandle(this);
        var parentWindow =
            CoreWebView2ControllerWindowReference.CreateFromWindowHandle(
                unchecked((ulong)windowHandle.ToInt64()));
        var controller = await _environment.CreateCoreWebView2ControllerAsync(
            parentWindow);
        if (_closed)
        {
            TryCloseController(controller);
            return;
        }

        _controller = controller;
        _webView = controller.CoreWebView2;
        controller.IsVisible = true;
        UpdateBrowserBounds();

        if (_headers.TryGetValue("User-Agent", out var userAgent)
            && !string.IsNullOrWhiteSpace(userAgent))
        {
            _webView.Settings.UserAgent = userAgent;
        }

        _webView.DocumentTitleChanged += OnDocumentTitleChanged;
        _webView.NavigationStarting += OnNavigationStarting;
        _webView.NavigationCompleted += OnNavigationCompleted;
        _webView.NewWindowRequested += OnNewWindowRequested;
        Navigate(_uri);
    }

    private async Task WaitForLayoutAsync()
    {
        if (_root.ActualWidth > 1 && _root.ActualHeight > 1)
        {
            return;
        }

        var layoutReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLayoutReady(object sender, SizeChangedEventArgs args)
        {
            if (args.NewSize.Width > 1 && args.NewSize.Height > 1)
            {
                layoutReady.TrySetResult();
            }
        }

        _root.SizeChanged += OnLayoutReady;
        try
        {
            await layoutReady.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            // Continue with the current measured size. The persistent
            // SizeChanged handler will update the controller after layout.
        }
        finally
        {
            _root.SizeChanged -= OnLayoutReady;
        }
    }

    private void Navigate(Uri uri)
    {
        var allHeaders = new Dictionary<string, string>(
            _headers,
            StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(_cookies))
        {
            allHeaders["Cookie"] = _cookies;
        }

        var headerText = string.Join(
            "\r\n",
            allHeaders.Select(header => $"{header.Key}: {header.Value}"));
        var request = _environment.CreateWebResourceRequest(
            uri.AbsoluteUri,
            "GET",
            null,
            headerText);
        _webView!.NavigateWithWebResourceRequest(request);
    }

    private void OnNavigationStarting(
        CoreWebView2 sender,
        CoreWebView2NavigationStartingEventArgs args)
    {
        if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri))
        {
            _address.Text = uri.Host;
        }

        _status.Text = "正在加载源站验证页面…";
    }

    private async void OnNavigationCompleted(
        CoreWebView2 sender,
        CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!args.IsSuccess)
        {
            _status.Text = $"页面加载失败：{args.WebErrorStatus}";
            return;
        }

        _status.Text = "页面已加载；AniMeido 正在检查登录状态。";
        try
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(750),
                _lifetimeCancellation.Token);
            if (!await PageStillNeedsInteractionAsync(sender))
            {
                await CheckAndContinueAsync();
            }
            else
            {
                _status.Text = "请完成登录或验证，然后点击“检查并继续”。";
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnDocumentTitleChanged(
        CoreWebView2 sender,
        object args)
    {
        var title = sender.DocumentTitle;
        Title = string.IsNullOrWhiteSpace(title)
            ? "AniMeido 源站验证"
            : $"{title} - AniMeido 源站验证";
    }

    private void OnNewWindowRequested(
        CoreWebView2 sender,
        CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
        {
            sender.Navigate(uri.AbsoluteUri);
        }
    }

    private async void OnCompleteClick(object sender, RoutedEventArgs args)
    {
        await CheckAndContinueAsync();
    }

    private void OnCancelClick(object sender, RoutedEventArgs args)
        => CloseWindow();

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs args)
    {
        TrackDpiChanges();
        UpdateBrowserBounds();
    }

    private void TrackDpiChanges()
    {
        var xamlRoot = _root.XamlRoot;
        if (xamlRoot is null || ReferenceEquals(xamlRoot, _trackedXamlRoot))
        {
            return;
        }

        _trackedXamlRoot = xamlRoot;
        _trackedXamlRoot.Changed += OnXamlRootChanged;
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
        => UpdateBrowserBounds();

    private void UpdateBrowserBounds()
    {
        if (_controller is null)
        {
            return;
        }

        var scale = _root.XamlRoot?.RasterizationScale ?? 1d;
        var top = _browserPlaceholder.TransformToVisual(_root)
            .TransformPoint(new Point()).Y;
        _controller.Bounds = new Rect(
            0,
            top * scale,
            Math.Max(1, _root.ActualWidth * scale),
            Math.Max(1, (_root.ActualHeight - top) * scale));
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _lifetimeCancellation.Cancel();
        Closed -= OnClosed;
        _root.SizeChanged -= OnRootSizeChanged;
        if (_trackedXamlRoot is not null)
        {
            _trackedXamlRoot.Changed -= OnXamlRootChanged;
            _trackedXamlRoot = null;
        }
        _completion.TrySetResult(false);
        if (_webView is not null)
        {
            _webView.DocumentTitleChanged -= OnDocumentTitleChanged;
            _webView.NavigationStarting -= OnNavigationStarting;
            _webView.NavigationCompleted -= OnNavigationCompleted;
            _webView.NewWindowRequested -= OnNewWindowRequested;
            _webView = null;
        }

        var controller = _controller;
        _controller = null;
        if (controller is not null)
        {
            TryCloseController(controller);
        }
    }

    private async Task CheckAndContinueAsync()
    {
        if (_checking || _closed)
        {
            return;
        }

        _checking = true;
        _retryButton.IsEnabled = false;
        _status.Text = "正在重试原播放请求…";
        try
        {
            if (await _verifyAsync(_lifetimeCancellation.Token))
            {
                _completion.TrySetResult(true);
                CloseWindow();
                return;
            }

            _status.Text =
                "尚未检测到登录或验证成功；窗口会保持打开，请完成后重试。";
        }
        catch (SourceResolutionException ex)
            when (ex.Kind == SourceResolutionFailureKind.Timeout)
        {
            _status.Text =
                "验证后的后台重试超时；请检查页面状态后再次尝试。";
        }
        catch (OperationCanceledException)
            when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
            when (ex is HttpRequestException
                or InvalidOperationException
                or JsonException)
        {
            _status.Text = $"尚未检测到登录成功：{ex.Message}";
        }
        finally
        {
            _checking = false;
            if (!_closed)
            {
                _retryButton.IsEnabled = true;
            }
        }
    }

    private static async Task<bool> PageStillNeedsInteractionAsync(
        CoreWebView2 webView)
    {
        try
        {
            var json = await webView.ExecuteScriptAsync(
                """
                (() => ({
                    url: location.href || "",
                    title: document.title || "",
                    text: (document.body?.innerText || "").slice(0, 8000),
                    hasPasswordInput: !!document.querySelector(
                        'input[type="password"]'),
                    hasChallengeElement: !!document.querySelector(
                        'iframe[src*="captcha"], iframe[src*="challenge"],'
                        + '[class*="captcha"], [id*="captcha"],'
                        + '[class*="turnstile"], [id*="turnstile"],'
                        + '[class*="cf-chl"], [id*="cf-chl"]')
                }))()
                """);
            var snapshot = JsonSerializer.Deserialize<WebPageSnapshot>(json);
            return snapshot is not null
                && WebPageInteractionClassifier.Classify(snapshot)
                    != WebPageInteractionKind.None;
        }
        catch (Exception ex)
            when (ex is COMException
                or InvalidOperationException
                or JsonException)
        {
            return true;
        }
    }

    private void CloseWindow()
    {
        if (_closed)
        {
            return;
        }

        try
        {
            Close();
        }
        catch (Exception ex)
            when (ex is COMException or InvalidOperationException)
        {
            _closed = true;
            _completion.TrySetResult(false);
            var controller = _controller;
            _controller = null;
            if (controller is not null)
            {
                TryCloseController(controller);
            }
        }
    }

    private void ResizeWindow()
    {
        DpiWindowSizing.Resize(this, 1050, 760);
    }

    private static void TryCloseController(CoreWebView2Controller controller)
    {
        try
        {
            controller.Close();
        }
        catch (Exception ex)
            when (ex is COMException or InvalidOperationException)
        {
            // Closing the parent window can invalidate its child controller.
        }
    }
}
