using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.UI.Input.Preview.Injection;
using Xunit;

namespace AniMeido.Desktop.Tests;

internal sealed class DesktopTestMouse(Grid root)
{
    private readonly InputInjector _mouse = Assert.IsType<InputInjector>(InputInjector.TryCreate(root));
    private int _x;
    private int _y;

    internal void MoveTo(FrameworkElement target, double? x = null, double? y = null)
    {
        var point = target.TransformToVisual(root).TransformPoint(
            new Point(x ?? target.ActualWidth / 2, y ?? target.ActualHeight / 2));
        var (nextX, nextY) = ((int)point.X, (int)point.Y);
        _mouse.InjectMouseInput([new() { MouseOptions = InjectedInputMouseOptions.Move, DeltaX = nextX - _x, DeltaY = nextY - _y }]);
        (_x, _y) = (nextX, nextY);
    }

    internal void Click(FrameworkElement target, double? x = null, double? y = null)
    {
        MoveTo(target, x, y);
        _mouse.InjectMouseInput([
            new() { MouseOptions = InjectedInputMouseOptions.LeftDown },
            new() { MouseOptions = InjectedInputMouseOptions.LeftUp, TimeOffsetInMilliseconds = 30 },
        ]);
    }

    internal void Key(Windows.System.VirtualKey key) => _mouse.InjectKeyboardInput([
        new() { VirtualKey = (ushort)key },
        new() { VirtualKey = (ushort)key, KeyOptions = InjectedInputKeyOptions.KeyUp },
    ]);
}
