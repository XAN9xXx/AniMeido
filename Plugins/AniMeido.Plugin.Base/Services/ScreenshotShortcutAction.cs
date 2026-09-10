using AniMeido.Contracts;
using AniMeido.Contracts.Desktop;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Views;
using Microsoft.UI.Dispatching;

namespace AniMeido.Plugin.Base.Services;

public sealed class ScreenshotShortcutAction :
    IGlobalShortcutAction,
    IDisposable
{
    private const int VirtualKeyF12 = 0x7B;
    private readonly ScreenshotArchiveService _screenshots;
    private readonly ArchiveService _archive;
    private readonly IAppWindowActivationService _windowActivation;
    private readonly IPluginNavigator _navigator;
    private readonly DispatcherQueue _dispatcher;
    private volatile bool _enabled = true;
    private readonly HashSet<CaptureFeedbackWindow> _feedbackWindows = [];
    private bool _disposed;

    public ScreenshotShortcutAction(
        ScreenshotArchiveService screenshots,
        ArchiveService archive,
        IAppWindowActivationService windowActivation,
        IPluginNavigator navigator)
    {
        _screenshots = screenshots;
        _archive = archive;
        _windowActivation = windowActivation;
        _navigator = navigator;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
    }

    public string Id => "animeido.screenshot.capture";

    public int VirtualKey => VirtualKeyF12;

    public bool IsEnabled => _enabled;

    public bool SuppressInput => true;

    public void SetEnabled(bool enabled) => _enabled = enabled;

    public async Task InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = await _archive.GetScreenshotSettingsAsync(
            cancellationToken);
        _enabled = settings.Enabled;
    }

    public async Task ExecuteAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var settings = await _archive.GetScreenshotSettingsAsync(
            cancellationToken);
        _enabled = settings.Enabled;
        if (!settings.Enabled)
        {
            return;
        }

        try
        {
            var screenshot = await _screenshots.CaptureAsync(
                cancellationToken);
            ShowFeedback(
                screenshot,
                settings,
                errorMessage: null);
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
                or NotSupportedException
                or IOException
                or UnauthorizedAccessException)
        {
            ShowFeedback(
                screenshot: null,
                settings,
                ex.Message);
        }
    }

    private void ShowFeedback(
        AnimeScreenshot? screenshot,
        ScreenshotSettings settings,
        string? errorMessage)
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed)
            {
                return;
            }

            if (screenshot is not null && settings.SoundEnabled)
            {
                ScreenshotSound.Play();
            }

            if (!settings.PopupEnabled)
            {
                return;
            }

            var window = new CaptureFeedbackWindow(
                screenshot,
                errorMessage,
                () =>
                {
                    if (screenshot is null)
                    {
                        return;
                    }

                    _windowActivation.ActivateMainWindow();
                    _navigator.Navigate(
                        typeof(ArchivePage),
                        screenshot.ScreenshotId);
                });
            _feedbackWindows.Add(window);
            window.Closed += (_, _) => _feedbackWindows.Remove(window);
            window.ShowWithoutActivation();
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _enabled = false;
        void CloseWindows()
        {
            foreach (var window in _feedbackWindows.ToArray())
            {
                window.Close();
            }

            _feedbackWindows.Clear();
        }

        if (_dispatcher.HasThreadAccess)
        {
            CloseWindows();
        }
        else
        {
            _dispatcher.TryEnqueue(CloseWindows);
        }
    }
}
