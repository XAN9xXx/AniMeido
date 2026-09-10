using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;

namespace AniMeido.Plugin.Base.Views;

internal sealed class CaptureFeedbackWindow : Window
{
    private const int GwlExStyle = -20;
    private const nint WsExToolWindow = 0x00000080;
    private const nint WsExNoActivate = 0x08000000;
    private const nint WsExTopmost = 0x00000008;
    private readonly Action _clicked;
    private Image? _previewImage;
    private readonly DispatcherTimer _timer = new()
    {
        Interval = TimeSpan.FromSeconds(3),
    };

    public CaptureFeedbackWindow(
        AnimeScreenshot? screenshot,
        string? errorMessage,
        Action clicked)
    {
        _clicked = clicked;
        Title = "AniMeido 截图";
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Padding = new Thickness(14),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (screenshot is not null)
        {
            _previewImage = new Image
            {
                Stretch = Stretch.UniformToFill,
            };
            ManagedImageLoader.ConfigureLocal(
                _previewImage,
                screenshot.FilePath,
                120);
            panel.Children.Add(new Border
            {
                Width = 120,
                Height = 68,
                CornerRadius = new CornerRadius(8),
                Child = _previewImage,
            });
        }

        var message = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 3,
            MaxWidth = screenshot is null ? 330 : 210,
        };
        message.Children.Add(new TextBlock
        {
            Text = errorMessage is null ? "截图已保存" : "截图失败",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        message.Children.Add(new TextBlock
        {
            Text = errorMessage is null
                ? "点击可在档案馆中查看"
                : errorMessage,
            Opacity = 0.68,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(message);

        var border = new Border
        {
            Background = new SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(248, 27, 29, 42)),
            BorderBrush = new SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(72, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = panel,
        };
        border.Tapped += (_, _) =>
        {
            _timer.Stop();
            Close();
            _clicked();
        };
        Content = border;
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Close();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            if (_previewImage is not null)
            {
                ManagedImageLoader.Cancel(_previewImage);
                _previewImage = null;
            }
        };
    }

    public void ShowWithoutActivation()
    {
        var handle = WindowNative.GetWindowHandle(this);
        var style = GetWindowLongPtr(handle, GwlExStyle);
        _ = SetWindowLongPtr(
            handle,
            GwlExStyle,
            style | WsExToolWindow | WsExNoActivate | WsExTopmost);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        const int width = 380;
        const int height = 104;
        var display = DisplayArea.GetFromWindowId(
            windowId,
            DisplayAreaFallback.Nearest);
        appWindow.MoveAndResize(new RectInt32(
            display.WorkArea.X + display.WorkArea.Width - width - 18,
            display.WorkArea.Y + display.WorkArea.Height - height - 18,
            width,
            height));
        appWindow.Show(activateWindow: false);
        _timer.Start();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(
        nint window,
        int index,
        nint value);
}
