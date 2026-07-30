using AniMeido.Plugin.Base.Models;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
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
            Padding = new Thickness(12),
        };
        if (screenshot is not null)
        {
            panel.Children.Add(new Image
            {
                Width = 120,
                Height = 68,
                Stretch = Stretch.UniformToFill,
                Source = new BitmapImage(new Uri(screenshot.FilePath)),
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = errorMessage is null
                ? "截图已保存"
                : $"截图失败\n{errorMessage}",
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 210,
        });
        var border = new Border
        {
            Background = new SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(245, 35, 35, 42)),
            CornerRadius = new CornerRadius(10),
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
        Closed += (_, _) => _timer.Stop();
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
        const int width = 360;
        const int height = 96;
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
