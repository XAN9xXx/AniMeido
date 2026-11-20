using System.Globalization;

using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Plugin.Base.Services;

internal static class RatingPresentation
{
    internal static string Explanation =>
        "综合五个平台的原始评分。中文与国际社区以各半为基准，再按评分人数调整；人数足够多后，影响力逐渐接近。"
        + "默认至少一个平台有 300 人评分才参与评分排序；标签结果页可开启低票选项，将门槛降到 50 人。"
        + $"\n算法版本：{CompositeRating.ParameterVersion}。反馈评分问题时，请附作品名称、评分截图和查看日期。";

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
        if (CompositeRating.Calculate(ratings, firstDate, today) is { } composite && composite.IsEligible(includeLowVotes: true))
        {
            var sampleNotice = composite.IsEligible() ? "" : "（评分人数较少）";
            lines.Add("综合评分　" + composite.Score.ToString("F1", CultureInfo.InvariantCulture) + sampleNotice);
        }
        foreach (var row in CreateRows(ratings, today))
        {
            var scale = row.Scale == "100" ? "满分 100，" : "";
            lines.Add($"{row.Source}　{row.Score} 分（{scale}{row.Votes} 人评分）");
        }
        return string.Join(Environment.NewLine, lines);
    }

    internal static IReadOnlyList<RatingSourceRow> CreateRows(IReadOnlyList<MetadataRating> ratings, DateOnly today)
    {
        var rows = new List<RatingSourceRow>();
        foreach (var (platform, name, format) in Platforms)
        {
            var rating = ratings.FirstOrDefault(rating => rating.Platform == platform && CompositeRating.IsAvailable(rating, today));
            if (rating is null) continue;
            rows.Add(new RatingSourceRow(name,
                rating.Score.GetValueOrDefault().ToString(format, CultureInfo.InvariantCulture),
                platform == "anilist" ? "100" : "10",
                rating.Votes.GetValueOrDefault().ToString("N0", CultureInfo.InvariantCulture)));
        }
        return rows;
    }
}
