using AniMeido.Contracts.Desktop;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace AniMeido.App.Services;

public sealed class AppWindowActivationService : IAppWindowActivationService
{
    private Window? _window;
    private AppWindow? _appWindow;

    internal void Attach(Window window, AppWindow appWindow)
    {
        _window = window;
        _appWindow = appWindow;
    }

    internal void Detach()
    {
        _window = null;
        _appWindow = null;
    }

    internal void HideMainWindow() => _appWindow?.Hide();

    public void ActivateMainWindow()
    {
        _appWindow?.Show();
        _window?.Activate();
    }
}
