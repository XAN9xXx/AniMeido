using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace AniMeido.Plugin.Base.Views;

internal sealed class CaptureFeedbackWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Image? _image;
    private bool _closed;

    internal CaptureFeedbackWindow(AnimeScreenshot? screenshot, string? errorMessage, Action clicked)
    {
        Title = "AniMeido 截图";
        var content = CreateContent(screenshot, errorMessage, out _image);
        Content = content;
        content.Tapped += (_, _) => { if (_closed) return; Close(); clicked(); };
        _timer.Tick += (_, _) => Close();
        Closed += (_, _) =>
        {
            _closed = true; _timer.Stop();
            if (_image is not null) ManagedImageLoader.Cancel(_image);
        };
    }

    internal static Border CreateContent(AnimeScreenshot? screenshot, string? errorMessage, out Image? image)
    {
        var resources = Application.Current.Resources;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        image = null;
        if (screenshot is not null)
        {
            image = new Image { Stretch = Stretch.UniformToFill };
            ManagedImageLoader.ConfigureLocal(image, screenshot.FilePath, 120);
            panel.Children.Add(new Border { Width = 120, Height = 68, CornerRadius = new(8), Child = image });
        }
        var message = new StackPanel { Spacing = 4, MaxWidth = screenshot is null ? 328 : 208, VerticalAlignment = VerticalAlignment.Center };
        message.Children.Add(new TextBlock { Text = errorMessage is null ? "截图已保存" : "截图失败",
            Style = (Style)resources["DesktopEmphasisTextStyle"] });
        message.Children.Add(new TextBlock { Text = errorMessage ?? "点击可在档案馆中查看", TextWrapping = TextWrapping.Wrap,
            Style = (Style)resources["DesktopDataTextStyle"] });
        panel.Children.Add(message);
        return new Border { Style = (Style)resources["AniMeidoCardStyle"], Padding = new(14), Child = panel };
    }

    public void ShowWithoutActivation()
    {
        var handle = DesktopShellServices.GetWindowHandle(this);
        var style = GetWindowLongPtr(handle, -20);
        _ = SetWindowLongPtr(handle, -20, style | 0x80 | 0x08000000 | 0x8);
        var area = DesktopShellServices.GetWorkArea(handle);
        AppWindow.Move(new PointInt32(area.X + area.Width - 398, area.Y + area.Height - 122));
        AppWindow.Resize(new SizeInt32(380, 104));
        AppWindow.Show(activateWindow: false);
        _timer.Start();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}
