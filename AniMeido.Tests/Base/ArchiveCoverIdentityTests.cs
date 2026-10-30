using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Views;

namespace AniMeido.Tests;

public sealed class ArchiveCoverIdentityTests
{
    private static readonly DateTimeOffset Date = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(42)]
    [InlineData(-7)]
    [InlineData(0)]
    public void ArchiveListItem_UsesItsArchiveIdentity(int animeId)
    {
        var archive = new AnimeArchive(animeId, "档案", null, "", Date, Date);
        var item = new ArchiveListItem(archive, [], 0, 0, null);

        Assert.Equal(animeId, ArchivePage.GetArchiveCoverAnimeId(item));
        Assert.NotEqual(animeId + 1, ArchivePage.GetArchiveCoverAnimeId(item));
    }

    [Theory]
    [InlineData(42)]
    [InlineData(-7)]
    [InlineData(0)]
    public void AnnualReviewMoment_UsesItsEntryIdentity(int animeId)
    {
        var entry = new ArchiveEntry("entry", animeId, Date, null, "感想", Date, Date);
        var moment = new AnnualReviewMoment(entry, "感想作品");

        Assert.Equal(animeId, ArchivePage.GetArchiveCoverAnimeId(moment));
        Assert.NotEqual(animeId + 1, ArchivePage.GetArchiveCoverAnimeId(moment));
    }

    [Theory]
    [InlineData(42)]
    [InlineData(-7)]
    [InlineData(0)]
    public void ArchiveReviewCover_UsesTheProjectedIdentity(int animeId)
    {
        var cover = new ArchiveReviewCover(animeId, "年度封面", Date);

        Assert.Equal(animeId, ArchivePage.GetArchiveCoverAnimeId(cover));
        Assert.NotEqual(animeId + 1, ArchivePage.GetArchiveCoverAnimeId(cover));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not an archive cover")]
    [InlineData(42)]
    public void UnrelatedContext_HasNoArchiveCoverIdentity(object? context)
    {
        Assert.Null(ArchivePage.GetArchiveCoverAnimeId(context));
    }
}
