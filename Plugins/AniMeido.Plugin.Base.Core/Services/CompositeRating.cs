using System.Globalization;

namespace AniMeido.Plugin.Base.Services;

internal readonly record struct CompositeRatingResult(
    double Score, long MaximumVotes, double ChineseShare, double? OverseasScore, string? StrongestOverseasPlatform)
{
    internal bool IsEligible(bool includeLowVotes = false) =>
        MaximumVotes >= (includeLowVotes ? CompositeRating.LowVoteMinimum : CompositeRating.DefaultMinimum);
}

internal static class CompositeRating
{
    internal const string ParameterVersion = "2026-11-08";
    internal const int DefaultMinimum = 300;
    internal const int LowVoteMinimum = 50;
    private const double VoteScale = 750;
    private const int AniListMaximumAgeDays = 15;

    internal static CompositeRatingResult? Calculate(IEnumerable<MetadataRating> ratings, string? firstDate, DateOnly today)
    {
        if (StartDate(firstDate) > today) return null;
        var valid = ratings.Where(rating => IsAvailable(rating, today)).Select(Prepare).ToArray();
        if (valid.Length == 0) return null;

        var chinese = Group.Create(valid.Where(rating => rating.Platform == "bangumi").ToArray());
        var overseas = Group.Create(valid.Where(rating => rating.Platform != "bangumi").ToArray());
        // 两组以各半为基准；最大来源控制组间份额，避免同组的平台数量直接放大份额。
        var totalStrength = chinese.Strength + overseas.Strength;
        var chineseShare = chinese.Strength / totalStrength;
        var score = chineseShare * chinese.Score + overseas.Strength / totalStrength * overseas.Score;
        return new(score, valid.Max(rating => rating.Votes), chineseShare,
            overseas.Strength > 0 ? overseas.Score : null, overseas.StrongestPlatform);
    }

    internal static double? ScoreForRanking(IEnumerable<MetadataRating> ratings, string? firstDate,
        DateOnly today, bool includeLowVotes = false) =>
        Calculate(ratings, firstDate, today) is { } result && result.IsEligible(includeLowVotes) ? result.Score : null;

    internal static bool IsAvailable(MetadataRating rating, DateOnly today) =>
        rating.Platform is "bangumi" or "mal" or "anilist" or "kitsu" or "ann"
        && rating.Score is double score && double.IsFinite(score) && score > 0
        && score <= (rating.Platform == "anilist" ? 100 : 10)
        && rating.Votes is > 0
        && (rating.Platform != "anilist"
            || DateOnly.TryParseExact(rating.FetchedOn, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var fetched) && fetched <= today
                && today.DayNumber - fetched.DayNumber <= AniListMaximumAgeDays);

    private static DateOnly? StartDate(string? value)
    {
        var full = value?.Length switch { 4 => value + "-01-01", 7 => value + "-01", 10 => value, _ => null };
        return DateOnly.TryParseExact(full, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : null;
    }

    private static PreparedRating Prepare(MetadataRating rating)
    {
        var votes = rating.Votes.GetValueOrDefault();
        var strength = votes / (votes + VoteScale);
        return new(rating.Platform, rating.Platform == "anilist" ? rating.Score!.Value / 10 : rating.Score!.Value,
            votes, strength * strength);
    }

    private sealed record PreparedRating(string Platform, double Score, long Votes, double Strength);

    private sealed record Group(double Score, double Strength, string? StrongestPlatform)
    {
        internal static Group Create(PreparedRating[] ratings)
        {
            if (ratings.Length == 0) return new(0, 0, null);
            if (ratings.Length == 1) return new(ratings[0].Score, ratings[0].Strength, ratings[0].Platform);
            var strongest = ratings.MaxBy(rating => rating.Strength)!;
            return new(ratings.Sum(rating => rating.Strength * rating.Score) / ratings.Sum(rating => rating.Strength),
                strongest.Strength, strongest.Platform);
        }
    }
}
