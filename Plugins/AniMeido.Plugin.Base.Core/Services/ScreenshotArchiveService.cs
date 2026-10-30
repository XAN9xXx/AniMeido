using AniMeido.Contracts.Desktop;
using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Base.Models;
using System.Security.Cryptography;

namespace AniMeido.Plugin.Base.Services;

public sealed class ScreenshotArchiveService
{
    private readonly ArchiveService _archive;
    private readonly IForegroundWindowCaptureService _capture;
    private readonly IActiveAnimePlaybackContextProvider _playbackContext;

    private readonly IScreenshotRecycleBin _recycleBin;

    public ScreenshotArchiveService(
        ArchiveService archive,
        IForegroundWindowCaptureService capture,
        IActiveAnimePlaybackContextProvider playbackContext,
        IScreenshotRecycleBin recycleBin)
    {
        _archive = archive;
        _capture = capture;
        _playbackContext = playbackContext;
        _recycleBin = recycleBin;
    }

    public async Task<AnimeScreenshot> CaptureAsync(
        CancellationToken cancellationToken = default)
    {
        using var operation = _archive.Access.BeginWrite();
        var settings = await _archive.GetScreenshotSettingsAsync(
            cancellationToken);
        if (!settings.Enabled)
        {
            throw new InvalidOperationException("F12 截图当前已关闭。");
        }

        var captured = await _capture.CaptureAsync(cancellationToken);
        ActiveAnimePlaybackContext? playback = null;
        try
        {
            playback = await _playbackContext.GetActiveContextAsync(
                cancellationToken);
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException)
        {
        }

        var id = Guid.NewGuid().ToString("N");
        var localTime = captured.CapturedAt.ToLocalTime();
        var directory = Path.Combine(
            settings.RootDirectory,
            localTime.ToString("yyyy"),
            localTime.ToString("MM"));
        Directory.CreateDirectory(directory);
        var fileName =
            $"{localTime:yyyyMMdd-HHmmss-fff}-{id[..8]}.png";
        var path = Path.Combine(directory, fileName);
        var temporaryPath = path + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(
                temporaryPath,
                captured.PngData,
                cancellationToken);
            File.Move(temporaryPath, path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        var hash = Convert.ToHexString(
            SHA256.HashData(captured.PngData));
        var screenshot = new AnimeScreenshot(
            id,
            path,
            hash,
            captured.CapturedAt,
            captured.WindowTitle,
            captured.ProcessName,
            captured.Width,
            captured.Height,
            playback?.AnimeId,
            playback?.Title,
            playback?.EpisodeNumber,
            playback?.PositionSeconds,
            string.Empty,
            true);
        try
        {
            await _archive.InsertScreenshotAsync(
                screenshot,
                cancellationToken);
        }
        catch
        {
            File.Delete(path);
            throw;
        }

        return screenshot;
    }

    public async Task<(int Deleted, int Failed)> DeleteManyAsync(
        IReadOnlyList<AnimeScreenshot> screenshots,
        CancellationToken cancellationToken = default)
    {
        using var operation = _archive.Access.BeginWrite();
        var deleted = 0;
        var failed = 0;
        foreach (var screenshot in screenshots)
        {
            try
            {
                await DeleteAsync(screenshot, cancellationToken);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }

        return (deleted, failed);
    }

    public async Task DeleteAsync(
        AnimeScreenshot screenshot,
        CancellationToken cancellationToken = default)
    {
        using var operation = _archive.Access.BeginWrite();
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(screenshot.FilePath))
        {
            _recycleBin.MoveToRecycleBin(screenshot.FilePath);
        }

        await _archive.DeleteScreenshotRecordAsync(
            screenshot.ScreenshotId,
            cancellationToken);
    }
}
