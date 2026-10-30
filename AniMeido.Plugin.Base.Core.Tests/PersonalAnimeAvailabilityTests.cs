using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class PersonalAnimeAvailabilityTests
{
    [Fact]
    public void Availability_DoesNotDependOnDescriptionOrSeason()
    {
        var available = new Anime(42, "作品", null, [], null, null, "资料不可用", 0, 0);
        Assert.False(PersonalAnimeDataSource.IsUnavailable(available));

        Anime unavailable = new UnavailableAnime(42, "作品");
        Assert.True(PersonalAnimeDataSource.IsUnavailable(unavailable));
        Assert.True(PersonalAnimeDataSource.IsUnavailable(
            unavailable with { Description = "任意文案", SeasonYear = 2026 }));
        Assert.True(PersonalAnimeDataSource.IsUnavailable(available with { ID = -42 }));
    }
}
