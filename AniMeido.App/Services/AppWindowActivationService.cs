using AniMeido.Contracts.Desktop;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace AniMeido.App.Services;

public sealed class AppWindowActivationService
    : IAppWindowActivationService, IWindowHandleProvider
{
    private Window? _window;
    private AppWindow? _appWindow;
    private nint _windowHandle;

    internal void Attach(Window window, AppWindow appWindow)
    {
        _window = window;
        _appWindow = appWindow;
#if UNO_SKIA
        _windowHandle = DesktopNativeWindow.GetHandle(window);
#else
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
#endif
    }

    internal void Detach()
    {
        _window = null;
        _appWindow = null;
        _windowHandle = 0;
    }

    public bool TryGetWindowHandle(out nint handle)
    {
        handle = _windowHandle;
        return handle != 0;
    }

    internal void HideMainWindow()
    {
#if UNO_SKIA
        DesktopNativeWindow.Hide(_windowHandle);
#else
        _appWindow?.Hide();
#endif
    }

    public void ActivateMainWindow()
    {
#if UNO_SKIA
        DesktopNativeWindow.Show(_windowHandle);
#else
        _appWindow?.Show();
#endif
        _window?.Activate();
    }
}
