using System.Globalization;

namespace AniMeido.Plugin.Base.Services;

internal readonly record struct CompositeRatingResult(double Score, double NominalChineseShare);

internal static class CompositeRating
{
    internal const string ParameterVersion = "2026-10-21";
    private const double Center = 6.33;
    private const double DiscountVotes = 50;
    private const int MinimumVotes = 50;
    private const int AniListMaximumAgeDays = 15;
    private const double HigherGroupShare = 0.6;
    private const double LowerGroupShare = 0.4;
    private const double ReferenceChineseShare = 0.5;

    private static readonly IReadOnlyDictionary<string, double> Offsets = new Dictionary<string, double>
    {
        ["bangumi"] = 0,
        ["mal"] = 0.657,
        ["anilist"] = 0.353,
        ["kitsu"] = 0.522,
        ["ann"] = 0.456,
    };

    internal static CompositeRatingResult? Calculate(IEnumerable<MetadataRating> ratings, string? firstDate, DateOnly today)
    {
        if (StartDate(firstDate) > today) return null;
        var valid = ratings.Where(rating => IsAvailable(rating, today)).Select(Prepare).ToArray();
        if (valid.Length == 0 || valid.Max(rating => rating.Votes) < MinimumVotes) return null;

        var chinese = Group.Create(valid.Where(rating => rating.IsChinese).ToArray());
        var overseas = Group.Create(valid.Where(rating => !rating.IsChinese).ToArray());
        var nominalChineseShare = chinese.Ratings.Length > 0 && overseas.Ratings.Length > 0 && chinese.Score < overseas.Score
            ? LowerGroupShare : HigherGroupShare;
        var weights = Weights(chinese, overseas, nominalChineseShare);
        var shrinkWeights = Weights(chinese, overseas, ReferenceChineseShare);
        double mixed = 0;
        double inverseRootVotes = 0;
        foreach (var (group, weight, shrinkWeight) in new[]
        {
            (chinese, weights.Chinese, shrinkWeights.Chinese),
            (overseas, weights.Overseas, shrinkWeights.Overseas),
        })
        {
            foreach (var rating in group.Ratings)
            {
                var withinGroup = rating.ReliabilitySquared / group.SumReliabilitySquared;
                mixed += weight * withinGroup * rating.ShiftedScore;
                inverseRootVotes += shrinkWeight * withinGroup / Math.Sqrt(rating.Votes);
            }
        }
        // 参考人数固定使用 50:50，避免高分组切换造成跳变，也不偏向任一社区的人数。
        var referenceVotes = 1 / (inverseRootVotes * inverseRootVotes);
        var shrink = referenceVotes / (referenceVotes + DiscountVotes);
        return new(Center + shrink * (mixed - Center), nominalChineseShare);
    }

    internal static bool IsAvailable(MetadataRating rating, DateOnly today) =>
        !string.IsNullOrEmpty(rating.Platform) && Offsets.ContainsKey(rating.Platform) && rating.Score is double score && double.IsFinite(score)
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

    private static (double Chinese, double Overseas) Weights(Group chinese, Group overseas, double alphaC)
    {
        var chineseWeight = alphaC * chinese.Strength;
        var overseasWeight = (1 - alphaC) * overseas.Strength;
        var total = chineseWeight + overseasWeight;
        return (chineseWeight / total, overseasWeight / total);
    }

    private static PreparedRating Prepare(MetadataRating rating)
    {
        var votes = rating.Votes.GetValueOrDefault();
        var score = rating.Score.GetValueOrDefault();
        var reliability = votes / (votes + DiscountVotes);
        return new(rating.Platform == "bangumi",
            (rating.Platform == "anilist" ? score / 10 : score) - Offsets[rating.Platform], votes, reliability * reliability);
    }

    private sealed record PreparedRating(bool IsChinese, double ShiftedScore, long Votes, double ReliabilitySquared);

    private sealed record Group(PreparedRating[] Ratings, double Score, double Strength, double SumReliabilitySquared)
    {
        internal static Group Create(PreparedRating[] ratings)
        {
            if (ratings.Length == 0) return new(ratings, 0, 0, 0);
            var sum = ratings.Sum(rating => rating.ReliabilitySquared);
            return new(ratings, ratings.Sum(rating => rating.ReliabilitySquared * rating.ShiftedScore) / sum,
                ratings.Max(rating => rating.ReliabilitySquared), sum);
        }
    }
}
