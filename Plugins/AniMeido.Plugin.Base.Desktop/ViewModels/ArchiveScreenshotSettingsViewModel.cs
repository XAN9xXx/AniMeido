using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Plugin.Base.ViewModels;

/// <summary>拥有一次截图设置编辑的接纳、读取和保存；弹窗由页面提供。</summary>
internal sealed class ArchiveScreenshotSettingsViewModel
{
    private readonly Func<CancellationToken, Task<ScreenshotSettings>> _read;
    private readonly Func<ScreenshotSettings, CancellationToken, Task> _save;

    public bool IsEditing { get; private set; }

    public ArchiveScreenshotSettingsViewModel(ArchiveService archive)
        : this(archive.GetScreenshotSettingsAsync, archive.SaveScreenshotSettingsAsync)
    {
    }

    internal ArchiveScreenshotSettingsViewModel(
        Func<CancellationToken, Task<ScreenshotSettings>> read,
        Func<ScreenshotSettings, CancellationToken, Task> save)
    {
        _read = read;
        _save = save;
    }

    public async Task<ScreenshotSettings?> EditAsync(
        Func<ScreenshotSettings, Task<ScreenshotSettings?>> show,
        CancellationToken cancellationToken)
    {
        if (IsEditing || cancellationToken.IsCancellationRequested)
            return null;

        IsEditing = true;
        try
        {
            var settings = await _read(cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return null;

            var updated = await show(settings);
            if (updated is null)
                return null;

            await _save(updated, CancellationToken.None);
            return updated;
        }
        finally
        {
            IsEditing = false;
        }
    }
}
