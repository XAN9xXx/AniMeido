using AniMeido.Contracts.Models;

namespace AniMeido.Plugin.Base.ViewModels;

internal enum AnimeReleasePhase
{
    Unknown,
    Upcoming,
    CurrentSeason,
    Past,
}

internal static class AnimeReleaseClassifier
{
    public static AnimeReleasePhase Classify(Anime anime, DateOnly today)
    {
        if (anime.AirDate is { } airDate)
        {
            if (airDate > today)
            {
                return AnimeReleasePhase.Upcoming;
            }

            return CompareSeason(
                airDate.Year,
                SeasonHelper.FromMonth(airDate.Month),
                today);
        }

        if (anime.SeasonYear <= 0 || anime.SeasonMonth <= 0)
        {
            return AnimeReleasePhase.Unknown;
        }

        return CompareSeason(
            anime.SeasonYear,
            SeasonHelper.FromMonth(anime.SeasonMonth),
            today);
    }

    public static string GetPhaseText(AnimeReleasePhase phase) => phase switch
    {
        AnimeReleasePhase.Upcoming => "未上映",
        AnimeReleasePhase.CurrentSeason => "本季",
        AnimeReleasePhase.Past => "往季",
        _ => "日期未知",
    };

    public static string GetMediaFormatText(AnimeMediaFormat format) =>
        format switch
        {
            AnimeMediaFormat.Television => "TV动画",
            AnimeMediaFormat.Movie => "动画电影",
            AnimeMediaFormat.Ova => "OVA",
            AnimeMediaFormat.Ona => "ONA / Web动画",
            _ => "其他动画",
        };

    /// <summary>封面角标用的短名；TV 是多数、未知没有信息量，都不标。</summary>
    public static string? GetMediaFormatBadgeText(AnimeMediaFormat format) =>
        format switch
        {
            AnimeMediaFormat.Movie => "剧场版",
            AnimeMediaFormat.Ova => "OVA",
            AnimeMediaFormat.Ona => "Web",
            _ => null,
        };

    /// <summary>形态筛选标签上的名称。</summary>
    public static string GetMediaFormatFilterText(AnimeMediaFormat format) =>
        format switch
        {
            AnimeMediaFormat.Television => "TV动画",
            AnimeMediaFormat.Movie => "动画电影",
            AnimeMediaFormat.Ova => "OVA",
            AnimeMediaFormat.Ona => "Web动画",
            _ => "其他",
        };

    private static AnimeReleasePhase CompareSeason(
        int year,
        Season season,
        DateOnly today)
    {
        var currentSeason = SeasonHelper.FromMonth(today.Month);
        var value = year * 4 + (int)season;
        var currentValue = today.Year * 4 + (int)currentSeason;
        if (value > currentValue)
        {
            return AnimeReleasePhase.Upcoming;
        }

        return value == currentValue
            ? AnimeReleasePhase.CurrentSeason
            : AnimeReleasePhase.Past;
    }
}
