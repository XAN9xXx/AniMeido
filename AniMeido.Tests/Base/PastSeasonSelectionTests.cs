using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Views;

namespace AniMeido.Tests;

public sealed class PastSeasonSelectionTests
{
    [Theory]
    [InlineData(2026, 1, 2025, Season.Fall)]
    [InlineData(2026, 4, 2026, Season.Winter)]
    [InlineData(2026, 7, 2026, Season.Spring)]
    [InlineData(2026, 10, 2026, Season.Summer)]
    public void GetLatestCompletedSeason_ExcludesCurrentAndFutureSeasons(
        int year,
        int month,
        int expectedYear,
        Season expectedSeason)
    {
        var actual = PastSeasonPage.GetLatestCompletedSeason(
            new DateTime(year, month, 1));

        Assert.Equal(expectedYear, actual.Year);
        Assert.Equal(expectedSeason, actual.Season);
    }
}
