using System.Globalization;

namespace AniMeido.Plugin.Base.Services;

internal static class RatingPresentation
{
    internal static async Task<string> GetRatingTextAsync(
        this MetadataDataSource source, int animeId, CancellationToken cancellationToken)
    {
        var details = await source.GetRatingDetailsAsync(animeId, cancellationToken);
        return details is null ? string.Empty : Format(details.Ratings, details.FirstDate, details.AsOf);
    }

    private static readonly (string Platform, string Name, string Format)[] Platforms =
    [
        ("bangumi", "Bangumi", "F1"),
        ("mal", "MyAnimeList", "F2"),
        ("anilist", "AniList", "F0"),
        ("kitsu", "Kitsu", "F2"),
        ("ann", "Anime News Network", "F2"),
    ];

    internal static string Format(IReadOnlyList<MetadataRating> ratings, string? firstDate, DateOnly today)
    {
        var lines = new List<string>();
        if (CompositeRating.Calculate(ratings, firstDate, today) is { } composite)
            lines.Add("综合评分　" + composite.Score.ToString("F1", CultureInfo.InvariantCulture));
        foreach (var (platform, name, format) in Platforms)
        {
            var rating = ratings.FirstOrDefault(rating => rating.Platform == platform && CompositeRating.IsAvailable(rating, today));
            if (rating is null) continue;
            var score = rating.Score.GetValueOrDefault().ToString(format, CultureInfo.InvariantCulture);
            var votes = rating.Votes.GetValueOrDefault().ToString("N0", CultureInfo.InvariantCulture);
            var scale = platform == "anilist" ? "满分 100，" : "";
            lines.Add($"{name}　{score} 分（{scale}{votes} 人评分）");
        }
        return string.Join(Environment.NewLine, lines);
    }
}
