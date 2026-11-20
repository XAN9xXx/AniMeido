using AniMeido.Plugin.Base.Models;

namespace AniMeido.Plugin.Base.ViewModels;

/// <summary>拥有图库的可见结果、详情身份和批选模式；控件选择仍由页面映射。</summary>
internal sealed class ArchiveGalleryViewModel
{
    public IReadOnlyList<AnimeScreenshot> VisibleScreenshots { get; private set; } = [];
    public string? DetailScreenshotId { get; private set; }
    public bool IsBatchMode { get; private set; }

    public AnimeScreenshot? DetailScreenshot => VisibleScreenshots.FirstOrDefault(
        item => item.ScreenshotId == DetailScreenshotId);

    public IReadOnlyList<AnimeScreenshot> ApplyFilter(
        IEnumerable<AnimeScreenshot> screenshots,
        IReadOnlyDictionary<string, IReadOnlyList<string>> tags,
        string filter,
        int? year,
        IReadOnlySet<string> previouslySelectedIds)
    {
        VisibleScreenshots = ArchiveBrowse.FilterScreenshots(screenshots, tags, filter.Trim(), year);
        if (IsBatchMode)
        {
            return VisibleScreenshots
                .Where(item => previouslySelectedIds.Contains(item.ScreenshotId))
                .ToArray();
        }

        var selected = VisibleScreenshots.FirstOrDefault(
            item => previouslySelectedIds.Contains(item.ScreenshotId))
            ?? VisibleScreenshots.FirstOrDefault();
        return selected is null ? [] : [selected];
    }

    public void CompleteFilterSelection(string? selectedScreenshotId)
    {
        if (!IsBatchMode)
        {
            DetailScreenshotId = selectedScreenshotId;
            return;
        }

        // 批选取消勾选不等于关闭预览；只有预览被筛掉才换成当前项或首项。
        if (DetailScreenshot is null)
            DetailScreenshotId = selectedScreenshotId ?? VisibleScreenshots.FirstOrDefault()?.ScreenshotId;
    }

    public void UpdateSelection(string? lastAddedScreenshotId, string? selectedScreenshotId)
    {
        if (lastAddedScreenshotId is not null)
            DetailScreenshotId = lastAddedScreenshotId;
        else if (selectedScreenshotId is not null)
            DetailScreenshotId = selectedScreenshotId;
        else if (!IsBatchMode)
            DetailScreenshotId = null;
    }

    public void ToggleBatchMode() => IsBatchMode = !IsBatchMode;
}
