using AniMeido.Plugin.Base.ViewModels;
using Xunit;

namespace AniMeido.Desktop.Tests;

public sealed class DesktopScreenshotPageTests
{
    [Theory]
    [InlineData(48, 1)]
    [InlineData(96, 2)]
    public void DeepLinkOpensThePageContainingItsScreenshot(int target, int expectedPage)
    {
        var screenshots = Enumerable.Range(0, 120).ToArray();

        var page = DesktopScreenshotPaging.SelectContaining(
            screenshots, item => item == target);

        Assert.Equal(expectedPage, page.Index);
        Assert.Contains(target, page.Items);
    }

    [Fact]
    public void DeepLinkUsesTheFilteredGalleryOrder()
    {
        var screenshots = Enumerable.Range(0, 150)
            .Select(id => (Id: id, AnimeId: id % 3 == 0 ? (int?)null : 7))
            .Where(item => item.AnimeId is null)
            .ToArray();

        var page = DesktopScreenshotPaging.SelectContaining(
            screenshots, item => item.Id == 147);

        Assert.Equal(1, page.Index);
        Assert.Contains(page.Items, item => item.Id == 147);
        Assert.All(page.Items, item => Assert.Null(item.AnimeId));
        Assert.Equal(50, page.TotalCount);
    }

    [Fact]
    public void MissingDeepLinkKeepsTheClampedFallbackPage()
    {
        var screenshots = Enumerable.Range(0, 60).ToArray();

        var page = DesktopScreenshotPaging.SelectContaining(
            screenshots, item => item == 100, 1);

        Assert.Equal(1, page.Index);
        Assert.Equal(Enumerable.Range(48, 12), page.Items);
    }

    [Fact]
    public void EmptyGalleryKeepsOneStableEmptyPage()
    {
        var page = DesktopScreenshotPaging.Select(Array.Empty<int>(), 8);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.Index);
        Assert.Equal(1, page.PageCount);
        Assert.Equal(0, page.TotalCount);
        Assert.False(page.HasPrevious);
        Assert.False(page.HasNext);
    }

    [Fact]
    public void GalleryLimitsEachPageAndKeepsTheSuppliedDateOrder()
    {
        var screenshots = Enumerable.Range(0, 97).Reverse().ToArray();

        var first = DesktopScreenshotPaging.Select(screenshots, 0);
        var second = DesktopScreenshotPaging.Select(screenshots, 1);

        Assert.Equal(screenshots.Take(48), first.Items);
        Assert.Equal(screenshots.Skip(48).Take(48), second.Items);
        Assert.Equal(97, first.TotalCount);
        Assert.Equal(3, first.PageCount);
        Assert.False(first.HasPrevious);
        Assert.True(first.HasNext);
        Assert.True(second.HasPrevious);
        Assert.True(second.HasNext);
    }

    [Fact]
    public void FilteringOrDeletingTheLastPageClampsTheCurrentPage()
    {
        var remaining = Enumerable.Range(0, 48).ToArray();

        var page = DesktopScreenshotPaging.Select(remaining, 2);

        Assert.Equal(0, page.Index);
        Assert.Equal(remaining, page.Items);
        Assert.False(page.HasNext);
    }

    [Fact]
    public void NegativePageStartsAtTheBeginningWithoutChangingTheInput()
    {
        var screenshots = Enumerable.Range(0, 60).ToArray();

        var page = DesktopScreenshotPaging.Select(screenshots, -1);

        Assert.Equal(0, page.Index);
        Assert.Equal(Enumerable.Range(0, 48), page.Items);
        Assert.Equal(Enumerable.Range(0, 60), screenshots);
    }

    [Fact]
    public void LastPartialPageKeepsEveryRemainingScreenshot()
    {
        var page = DesktopScreenshotPaging.Select(
            Enumerable.Range(0, 97).ToArray(), 2);

        Assert.Equal([96], page.Items);
        Assert.Equal(2, page.Index);
        Assert.True(page.HasPrevious);
        Assert.False(page.HasNext);
    }
}
