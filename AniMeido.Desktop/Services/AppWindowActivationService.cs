using AniMeido.Contracts.Desktop;
using Microsoft.UI.Xaml;

namespace AniMeido.App.Services;

public sealed class AppWindowActivationService
    : IAppWindowActivationService, IWindowHandleProvider
{
    private Window? _window;
    private nint _windowHandle;

    internal void Attach(Window window)
    {
        _window = window;
        _windowHandle = DesktopNativeWindow.GetHandle(window);
    }

    internal void Detach()
    {
        _window = null;
        _windowHandle = 0;
    }

    public bool TryGetWindowHandle(out nint handle)
    {
        handle = _windowHandle;
        return handle != 0;
    }

    internal void HideMainWindow()
    {
        DesktopNativeWindow.Hide(_windowHandle);
    }

    public void ActivateMainWindow()
    {
        DesktopNativeWindow.Show(_windowHandle);
        _window?.Activate();
    }
}
