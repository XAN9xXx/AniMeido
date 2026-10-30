using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

public sealed class ArchiveBrowseTests
{
    private static readonly DateTimeOffset Date = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ArchiveFilters_CombineCriteriaAndPreserveOrder()
    {
        var first = Archive(1, "第一部", AnimeTrackingStatus.Completed, ["奇幻"]);
        var second = Archive(2, "奇幻作品", AnimeTrackingStatus.Watching, []);
        var blocked = Archive(3, "屏蔽", AnimeTrackingStatus.Blocked, ["奇幻"]);
        ArchiveListItem[] all = [second, blocked, first];

        Assert.Equal([second, first], ArchiveBrowse.FilterArchives(all, "奇幻", null, null, null));
        Assert.Equal([first], ArchiveBrowse.FilterArchives(all, "奇幻", AnimeTrackingStatus.Completed, 8, 2026));
        Assert.Empty(ArchiveBrowse.FilterArchives(all, "", null, 9, null));
        Assert.Empty(ArchiveBrowse.FilterArchives(all, "", null, null, 2025));
    }

    [Theory]
    [InlineData("作品")]
    [InlineData("备注")]
    [InlineData("PLAYER")]
    [InlineData("标签")]
    public void ScreenshotFilter_SearchesAllExistingFields(string filter)
    {
        var screenshot = new AnimeScreenshot("shot", "fixture.png", "", Date, "", "player", 1, 1,
            1, "作品", null, null, "备注", true);
        var tags = new Dictionary<string, IReadOnlyList<string>> { ["shot"] = ["标签"] };
        Assert.Equal([screenshot], ArchiveBrowse.FilterScreenshots([screenshot], tags, filter, 2026));
        Assert.Empty(ArchiveBrowse.FilterScreenshots([screenshot], tags, filter, 2025));
    }

    [Fact]
    public void Timeline_PreservesChronologyAndOptionalPlayback()
    {
        var entry = new ArchiveEntry("entry", 1, Date, 2, "感想", Date, Date);
        var history = new WatchHistoryItem("watch", 1, "作品", Date.AddDays(1), 1, 2, null, "", true);
        var change = new ArchiveTrackingChange(Date.AddDays(2), AnimeTrackingStatus.Completed);

        var withoutPlayer = ArchiveBrowse.BuildTimeline([entry], [history], [change], false);
        Assert.Equal(["状态变更", "感想"], withoutPlayer.Select(item => item.Kind));
        Assert.Equal("标记为已看完", withoutPlayer[0].Heading);
        Assert.Equal(entry, withoutPlayer[1].Entry);

        var withPlayer = ArchiveBrowse.BuildTimeline([entry], [history], [change], true);
        Assert.Equal(["状态变更", "观看记录", "感想"], withPlayer.Select(item => item.Kind));
    }

    private static ArchiveListItem Archive(int id, string title, AnimeTrackingStatus status, string[] tags)
        => new(new AnimeArchive(id, title, 8, "", Date, Date), tags, 0, 0, status);
}
