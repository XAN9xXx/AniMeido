namespace AniMeido.Contracts.Desktop;

/// <summary>
/// A plugin-owned action invoked by an App-hosted global keyboard listener.
/// </summary>
public interface IGlobalShortcutAction
{
    string Id { get; }

    int VirtualKey { get; }

    bool IsEnabled { get; }

    bool SuppressInput { get; }

    Task InitializeAsync(
        CancellationToken cancellationToken = default);

    Task ExecuteAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// App-hosted foreground-window capture capability.
/// </summary>
public interface IForegroundWindowCaptureService
{
    bool IsSupported { get; }

    Task<ForegroundWindowCapture> CaptureAsync(
        CancellationToken cancellationToken = default);
}

public sealed record ForegroundWindowCapture(
    byte[] PngData,
    string WindowTitle,
    string ProcessName,
    int Width,
    int Height,
    DateTimeOffset CapturedAt);

/// <summary>
/// Restores and activates the App's primary window.
/// </summary>
public interface IAppWindowActivationService
{
    void ActivateMainWindow();
}
