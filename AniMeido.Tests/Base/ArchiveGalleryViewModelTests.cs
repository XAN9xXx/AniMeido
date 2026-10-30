using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

public sealed class ArchiveGalleryViewModelTests
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoTags =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    [Fact]
    public void NewGallery_HasNoVisibleSelectionOrBatchMode()
    {
        var gallery = new ArchiveGalleryViewModel();

        Assert.Empty(gallery.VisibleScreenshots);
        Assert.Null(gallery.DetailScreenshotId);
        Assert.Null(gallery.DetailScreenshot);
        Assert.False(gallery.IsBatchMode);
    }

    [Fact]
    public void Filter_PreservesSourceOrderAndIncludesMissingFiles()
    {
        var first = Screenshot("first") with { CapturedAt = CapturedAt.AddDays(-1), FileExists = false };
        var second = Screenshot("second") with { CapturedAt = CapturedAt.AddDays(1) };
        AnimeScreenshot[] screenshots = [first, second];
        var gallery = new ArchiveGalleryViewModel();

        var restored = gallery.ApplyFilter(screenshots, NoTags, "", null, Selected());

        Assert.Equal(screenshots, gallery.VisibleScreenshots);
        Assert.Same(first, Assert.Single(restored));
        gallery.CompleteFilterSelection(first.ScreenshotId);
        Assert.Same(first, gallery.DetailScreenshot);
    }

    [Theory]
    [InlineData("作品")]
    [InlineData("备注")]
    [InlineData("PLAYER")]
    [InlineData("个人标签")]
    public void Filter_TrimsInputSearchesExistingFieldsAndCombinesTheYear(string filter)
    {
        var matching = Screenshot("matching") with
        {
            AnimeTitle = "目标作品",
            ContextNote = "截图备注",
            ProcessName = "player",
        };
        var old = matching with
        {
            ScreenshotId = "old",
            CapturedAt = CapturedAt.AddYears(-1),
        };
        var tags = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [matching.ScreenshotId] = ["个人标签"],
            [old.ScreenshotId] = ["个人标签"],
        };
        var gallery = new ArchiveGalleryViewModel();

        var restored = gallery.ApplyFilter(
            [old, matching, Screenshot("other")], tags, $"  {filter}  ", CapturedAt.ToLocalTime().Year, Selected());

        Assert.Same(matching, Assert.Single(gallery.VisibleScreenshots));
        Assert.Same(matching, Assert.Single(restored));
    }

    [Fact]
    public void SingleFilter_KeepsAVisiblePreviousSelectionAndFallsBackToTheFirstItem()
    {
        var first = Screenshot("first");
        var second = Screenshot("second");
        var gallery = new ArchiveGalleryViewModel();

        var preserved = gallery.ApplyFilter([first, second], NoTags, "", null, Selected(second.ScreenshotId));
        Assert.Same(second, Assert.Single(preserved));
        gallery.CompleteFilterSelection(second.ScreenshotId);
        Assert.Same(second, gallery.DetailScreenshot);

        var fallback = gallery.ApplyFilter([first], NoTags, "", null, Selected(second.ScreenshotId));
        Assert.Same(first, Assert.Single(fallback));
        gallery.CompleteFilterSelection(first.ScreenshotId);
        Assert.Same(first, gallery.DetailScreenshot);
    }

    [Fact]
    public void SingleFilter_UsesTheActualControlSelectionAfterRendering()
    {
        var first = Screenshot("first");
        var second = Screenshot("second");
        var gallery = new ArchiveGalleryViewModel();
        var restored = gallery.ApplyFilter([first, second], NoTags, "", null, Selected());
        Assert.Same(first, Assert.Single(restored));

        gallery.CompleteFilterSelection(second.ScreenshotId);

        Assert.Equal(second.ScreenshotId, gallery.DetailScreenshotId);
        Assert.Same(second, gallery.DetailScreenshot);
    }

    [Fact]
    public void ClearingFilter_KeepsTheCurrentSelectionRatherThanRestoringHiddenHistory()
    {
        var first = Screenshot("first") with { AnimeTitle = "第一张" };
        var second = Screenshot("second") with { AnimeTitle = "第二张" };
        AnimeScreenshot[] screenshots = [first, second];
        var gallery = new ArchiveGalleryViewModel();
        gallery.ApplyFilter(screenshots, NoTags, "", null, Selected(second.ScreenshotId));
        gallery.CompleteFilterSelection(second.ScreenshotId);

        var filtered = gallery.ApplyFilter(screenshots, NoTags, "第一", null, Selected(second.ScreenshotId));
        Assert.Same(first, Assert.Single(filtered));
        gallery.CompleteFilterSelection(first.ScreenshotId);
        var cleared = gallery.ApplyFilter(screenshots, NoTags, "", null, Selected(first.ScreenshotId));
        gallery.CompleteFilterSelection(first.ScreenshotId);

        Assert.Same(first, Assert.Single(cleared));
        Assert.Same(first, gallery.DetailScreenshot);
    }

    [Fact]
    public void EmptyFilterResult_ClearsSingleSelectionAndDetails()
    {
        var screenshot = Screenshot("first");
        var gallery = new ArchiveGalleryViewModel();
        gallery.ApplyFilter([screenshot], NoTags, "", null, Selected());
        gallery.CompleteFilterSelection(screenshot.ScreenshotId);

        var restored = gallery.ApplyFilter([screenshot], NoTags, "找不到", null, Selected(screenshot.ScreenshotId));
        gallery.CompleteFilterSelection(null);

        Assert.Empty(restored);
        Assert.Empty(gallery.VisibleScreenshots);
        Assert.Null(gallery.DetailScreenshotId);
        Assert.Null(gallery.DetailScreenshot);
    }

    [Fact]
    public void BatchFilter_RestoresOnlyVisibleSelectedItemsInSourceOrder()
    {
        var first = Screenshot("first") with { AnimeTitle = "保留一" };
        var hidden = Screenshot("hidden") with { AnimeTitle = "隐藏" };
        var last = Screenshot("last") with { AnimeTitle = "保留二" };
        var gallery = new ArchiveGalleryViewModel();
        gallery.ToggleBatchMode();

        var restored = gallery.ApplyFilter(
            [first, hidden, last], NoTags, "保留", null,
            Selected(last.ScreenshotId, hidden.ScreenshotId, first.ScreenshotId, "missing"));

        Assert.Equal([first, last], gallery.VisibleScreenshots);
        Assert.Equal([first, last], restored);
    }

    [Fact]
    public void BatchFilter_KeepsTheVisibleDetailEvenWhenItIsNotSelected()
    {
        var first = Screenshot("first");
        var detail = Screenshot("detail");
        var gallery = new ArchiveGalleryViewModel();
        gallery.ApplyFilter([first, detail], NoTags, "", null, Selected(detail.ScreenshotId));
        gallery.CompleteFilterSelection(detail.ScreenshotId);
        gallery.ToggleBatchMode();

        var restored = gallery.ApplyFilter([first, detail], NoTags, "", null, Selected(first.ScreenshotId));
        gallery.CompleteFilterSelection(first.ScreenshotId);

        Assert.Same(first, Assert.Single(restored));
        Assert.Same(detail, gallery.DetailScreenshot);
    }

    [Fact]
    public void BatchFilter_WhenDetailDisappearsUsesTheActualCurrentItem()
    {
        var first = Screenshot("first") with { AnimeTitle = "保留一" };
        var removed = Screenshot("removed") with { AnimeTitle = "移除" };
        var last = Screenshot("last") with { AnimeTitle = "保留二" };
        var gallery = new ArchiveGalleryViewModel();
        gallery.ApplyFilter([first, removed, last], NoTags, "", null, Selected(removed.ScreenshotId));
        gallery.CompleteFilterSelection(removed.ScreenshotId);
        gallery.ToggleBatchMode();

        gallery.ApplyFilter([first, removed, last], NoTags, "保留", null, Selected(first.ScreenshotId, last.ScreenshotId));
        gallery.CompleteFilterSelection(last.ScreenshotId);

        Assert.Same(last, gallery.DetailScreenshot);
    }

    [Fact]
    public void BatchFilter_WhenDetailDisappearsAndNothingIsSelectedUsesTheFirstVisibleItem()
    {
        var first = Screenshot("first");
        var removed = Screenshot("removed");
        var gallery = new ArchiveGalleryViewModel();
        gallery.ApplyFilter([first, removed], NoTags, "", null, Selected(removed.ScreenshotId));
        gallery.CompleteFilterSelection(removed.ScreenshotId);
        gallery.ToggleBatchMode();

        gallery.ApplyFilter([first], NoTags, "", null, Selected(removed.ScreenshotId));
        gallery.CompleteFilterSelection(null);

        Assert.Same(first, gallery.DetailScreenshot);
    }

    [Fact]
    public void SelectionChange_PrefersTheLastAddedItemThenTheActualCurrentItem()
    {
        var first = Screenshot("first");
        var last = Screenshot("last");
        var gallery = new ArchiveGalleryViewModel();
        gallery.ApplyFilter([first, last], NoTags, "", null, Selected());

        gallery.UpdateSelection(last.ScreenshotId, first.ScreenshotId);
        Assert.Same(last, gallery.DetailScreenshot);

        gallery.UpdateSelection(null, first.ScreenshotId);
        Assert.Same(first, gallery.DetailScreenshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptySelectionChange_ClearsSingleDetailsButKeepsBatchDetails(bool batchMode)
    {
        var screenshot = Screenshot("first");
        var gallery = new ArchiveGalleryViewModel();
        gallery.ApplyFilter([screenshot], NoTags, "", null, Selected());
        gallery.CompleteFilterSelection(screenshot.ScreenshotId);
        if (batchMode)
            gallery.ToggleBatchMode();

        gallery.UpdateSelection(null, null);

        if (batchMode)
            Assert.Same(screenshot, gallery.DetailScreenshot);
        else
            Assert.Null(gallery.DetailScreenshot);
    }

    [Fact]
    public void TogglingBatchMode_DoesNotChangeTheDetailIdentity()
    {
        var screenshot = Screenshot("first");
        var gallery = new ArchiveGalleryViewModel();
        gallery.ApplyFilter([screenshot], NoTags, "", null, Selected());
        gallery.CompleteFilterSelection(screenshot.ScreenshotId);

        gallery.ToggleBatchMode();
        Assert.True(gallery.IsBatchMode);
        Assert.Same(screenshot, gallery.DetailScreenshot);
        gallery.ToggleBatchMode();
        Assert.False(gallery.IsBatchMode);
        Assert.Same(screenshot, gallery.DetailScreenshot);
    }

    [Fact]
    public void RefreshingTheSameScreenshot_UsesTheNewRecordEvenWhenItsAssociatedWorkChanges()
    {
        var old = Screenshot("same") with { AnimeId = 42, AnimeTitle = "原作品" };
        var updated = old with { AnimeId = 43, AnimeTitle = "新作品", ContextNote = "新备注" };
        var gallery = new ArchiveGalleryViewModel();
        gallery.ApplyFilter([old], NoTags, "", null, Selected());
        gallery.CompleteFilterSelection(old.ScreenshotId);

        var restored = gallery.ApplyFilter([updated], NoTags, "", null, Selected(old.ScreenshotId));
        gallery.CompleteFilterSelection(updated.ScreenshotId);

        Assert.Same(updated, Assert.Single(restored));
        Assert.Same(updated, gallery.DetailScreenshot);
        Assert.Equal("same", gallery.DetailScreenshotId);
    }

    [Fact]
    public void BatchRefreshAfterPartialDeletion_RestoresSurvivorsAndClearsDetailsWhenAllAreRemoved()
    {
        var removed = Screenshot("removed");
        var survivor = Screenshot("survivor");
        var gallery = new ArchiveGalleryViewModel();
        gallery.ApplyFilter([removed, survivor], NoTags, "", null, Selected(removed.ScreenshotId));
        gallery.CompleteFilterSelection(removed.ScreenshotId);
        gallery.ToggleBatchMode();

        var restored = gallery.ApplyFilter(
            [survivor], NoTags, "", null, Selected(removed.ScreenshotId, survivor.ScreenshotId));
        gallery.CompleteFilterSelection(survivor.ScreenshotId);
        Assert.Same(survivor, Assert.Single(restored));
        Assert.Same(survivor, gallery.DetailScreenshot);

        var empty = gallery.ApplyFilter([], NoTags, "", null, Selected(survivor.ScreenshotId));
        gallery.CompleteFilterSelection(null);
        Assert.Empty(empty);
        Assert.Empty(gallery.VisibleScreenshots);
        Assert.Null(gallery.DetailScreenshotId);
        Assert.Null(gallery.DetailScreenshot);
    }

    [Fact]
    public void SelectingOrTogglingBatchMode_DoesNotRebuildTheVisibleList()
    {
        var first = Screenshot("first");
        var second = Screenshot("second");
        var gallery = new ArchiveGalleryViewModel();
        gallery.ApplyFilter([first, second], NoTags, "", null, Selected());
        gallery.CompleteFilterSelection(first.ScreenshotId);
        var visible = gallery.VisibleScreenshots;

        gallery.UpdateSelection(second.ScreenshotId, second.ScreenshotId);
        Assert.Same(visible, gallery.VisibleScreenshots);
        gallery.ToggleBatchMode();
        Assert.Same(visible, gallery.VisibleScreenshots);
        gallery.ToggleBatchMode();
        Assert.Same(visible, gallery.VisibleScreenshots);
    }

    private static HashSet<string> Selected(params string[] ids)
        => new(ids, StringComparer.Ordinal);

    private static AnimeScreenshot Screenshot(string id)
        => new(id, $"synthetic-{id}.png", "", CapturedAt, "", "viewer", 640, 360,
            null, id, null, null, "", true);
}
