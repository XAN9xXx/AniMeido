using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

public sealed class AnimeReleaseClassifierTests
{
    [Fact]
    public void FutureAirDate_IsUpcomingInsteadOfPast()
    {
        var anime = CreateAnime(
            new DateOnly(2026, 10, 9),
            2026,
            10);

        var phase = AnimeReleaseClassifier.Classify(
            anime,
            new DateOnly(2026, 8, 1));

        Assert.Equal(AnimeReleasePhase.Upcoming, phase);
    }

    [Theory]
    [InlineData(2026, 7, nameof(AnimeReleasePhase.CurrentSeason))]
    [InlineData(2026, 4, nameof(AnimeReleasePhase.Past))]
    [InlineData(2026, 10, nameof(AnimeReleasePhase.Upcoming))]
    public void MissingAirDate_FallsBackToSeasonMetadata(
        int year,
        int seasonMonth,
        string expected)
    {
        var anime = CreateAnime(null, year, seasonMonth);

        var phase = AnimeReleaseClassifier.Classify(
            anime,
            new DateOnly(2026, 8, 1));

        Assert.Equal(expected, phase.ToString());
    }

    [Fact]
    public void MissingDateAndSeason_IsUnknown()
    {
        var phase = AnimeReleaseClassifier.Classify(
            CreateAnime(null, 0, 0),
            new DateOnly(2026, 8, 1));

        Assert.Equal(AnimeReleasePhase.Unknown, phase);
    }

    private static Anime CreateAnime(
        DateOnly? airDate,
        int year,
        int seasonMonth)
        => new(
            1,
            "测试动画",
            null,
            [],
            airDate,
            null,
            string.Empty,
            year,
            seasonMonth);
}
