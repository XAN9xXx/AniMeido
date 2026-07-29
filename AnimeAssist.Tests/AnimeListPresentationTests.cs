using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

public sealed class AnimeListPresentationTests
{
    [Fact]
    public void Filter_AppliesBlockedAndTitleRulesTogether()
    {
        var anime = new[]
        {
            CreateAnime(1, "Alpha", 1),
            CreateAnime(2, "Alpha blocked", 2),
            CreateAnime(3, "Beta", 3),
        };

        var visible = AnimeListPresentation.Filter(
            anime,
            new HashSet<int> { 2 },
            "ALPHA");

        Assert.Collection(
            visible,
            item => Assert.Equal(1, item.ID));
    }

    [Fact]
    public void Filter_AllowsBlockedOnlyForDedicatedViews()
    {
        var anime = new[] { CreateAnime(1, "Blocked", 1) };

        var visible = AnimeListPresentation.Filter(
            anime,
            new HashSet<int> { 1 },
            includeBlocked: true);

        Assert.Single(visible);
    }

    [Fact]
    public void GroupByWeekday_UsesBangumiOrderAndLabels()
    {
        var groups = AnimeListPresentation.GroupByWeekday(
        [
            CreateAnime(1, "Sunday", 7),
            CreateAnime(2, "Monday", 1),
            CreateAnime(3, "Unknown", null),
        ]);

        Assert.Equal(["周一", "周日", "其他"],
            groups.Select(group => group.WeekdayName));
    }

    [Theory]
    [InlineData(DayOfWeek.Monday, 1)]
    [InlineData(DayOfWeek.Sunday, 7)]
    public void ToBangumiWeekday_UsesOneThroughSeven(
        DayOfWeek day,
        int expected)
    {
        Assert.Equal(
            expected,
            AnimeListPresentation.ToBangumiWeekday(day));
    }

    private static Anime CreateAnime(
        int id,
        string title,
        int? weekday) =>
        new(
            id,
            title,
            null,
            [],
            null,
            null,
            string.Empty,
            2026,
            7,
            weekday);
}
