using AniMeido.Contracts.Playback;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AniMeido.Plugin.Player.Sources;

internal static partial class TitleMatcher
{
    public static IReadOnlyList<SourceAnimeCandidate> Rank(
        AnimePlaybackContext anime,
        IEnumerable<SourceAnimeCandidate> candidates)
    {
        var expected = GetSearchTitles(anime)
            .Where(title => Normalize(title).Length > 0)
            .ToArray();
        return candidates
            .Select(candidate => new
            {
                Candidate = candidate,
                Score = Score(expected, candidate.Title),
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Candidate.Title.Length)
            .Select(item => item.Candidate)
            .ToArray();
    }

    public static bool IsConfident(
        AnimePlaybackContext anime,
        SourceAnimeCandidate candidate)
    {
        return GetSearchTitles(anime)
            .Any(title => SeasonPartIsCompatible(title, candidate.Title)
                && string.Equals(
                    Normalize(title),
                    Normalize(candidate.Title),
                    StringComparison.Ordinal));
    }

    public static IReadOnlyList<string> GetSearchTitles(
        AnimePlaybackContext anime)
        => new[] { anime.Title }
            .Concat(anime.AlternateTitles ?? [])
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Select(title => title.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static int Score(
        IReadOnlyList<string> expected,
        string candidateTitle)
    {
        var candidate = Normalize(candidateTitle);
        if (candidate.Length == 0)
        {
            return 0;
        }

        var score = 0;
        foreach (var expectedTitle in expected)
        {
            if (!SeasonPartIsCompatible(expectedTitle, candidateTitle))
            {
                continue;
            }

            var title = Normalize(expectedTitle);
            if (string.Equals(title, candidate, StringComparison.Ordinal))
            {
                score = Math.Max(score, 100);
            }
            else if (title.Contains(candidate, StringComparison.Ordinal)
                || candidate.Contains(title, StringComparison.Ordinal))
            {
                score = Math.Max(score, 60);
            }
        }

        return score;
    }

    private static bool SeasonPartIsCompatible(string left, string right)
    {
        var leftTokens = ExtractSeasonPartTokens(left);
        var rightTokens = ExtractSeasonPartTokens(right);
        return leftTokens.Length == 0
            || rightTokens.Length == 0
            || leftTokens.Intersect(
                rightTokens,
                StringComparer.Ordinal).Any();
    }

    private static string[] ExtractSeasonPartTokens(string value)
        => SeasonPartRegex().Matches(value)
            .Select(match =>
            {
                var kind = match.Groups["kind"].Value;
                var normalizedKind = kind.Equals(
                        "part",
                        StringComparison.OrdinalIgnoreCase)
                    || kind == "部"
                        ? "part"
                        : "season";
                return $"{normalizedKind}:{match.Groups["number"].Value}";
            })
            .ToArray();

    private static string Normalize(string value)
    {
        var normalized = SeasonPartRegex().Replace(value, string.Empty)
            .Normalize(NormalizationForm.FormKC)
            .ToLower(CultureInfo.InvariantCulture);
        return IgnoredCharactersRegex().Replace(normalized, string.Empty);
    }

    [GeneratedRegex(@"[\s\p{P}\p{S}]+", RegexOptions.CultureInvariant)]
    private static partial Regex IgnoredCharactersRegex();

    [GeneratedRegex(
        @"(?:(?<kind>season|part)\s*(?<number>\d+)|第\s*(?<number>\d+)\s*(?<kind>[季期部]))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonPartRegex();
}
