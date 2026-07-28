using AniMeido.Plugin.Player.Playback;
using AniMeido.Plugin.Player.Sources;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AniMeido.Plugin.Player.Models;

internal sealed record PlayerEpisodeGroup(
    string Key,
    string DisplayTitle,
    double SortOrder,
    IReadOnlyList<SourceEpisodeEntry> Routes)
{
    private static readonly Regex EpisodeNumberPattern = new(
        @"^\s*(?:第\s*)?(?<number>\d+(?:\.\d+)?)\s*(?:集|话|話|回|EP(?:ISODE)?)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<PlayerEpisodeGroup> Create(
        IEnumerable<SourceEpisodeEntry> entries,
        IReadOnlyDictionary<string, RouteHealthRecord>? health = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries
            .GroupBy(CreateIdentity)
            .Select(group =>
            {
                var first = group.First();
                var routes = group
                    .OrderByDescending(entry => GetHealthScore(entry, health))
                    .ThenBy(entry => entry.SourceName, StringComparer.CurrentCulture)
                    .ThenBy(
                        entry => entry.Episode.Route,
                        StringComparer.CurrentCulture)
                    .ToArray();
                return new PlayerEpisodeGroup(
                    group.Key.Key,
                    group.Key.DisplayTitle,
                    group.Key.SortOrder,
                    routes);
            })
            .OrderBy(group => group.SortOrder)
            .ThenBy(group => group.DisplayTitle, StringComparer.CurrentCulture)
            .ToArray();
    }

    public override string ToString() => DisplayTitle;

    internal static string GetRouteKey(SourceEpisodeEntry entry)
        => $"{entry.Episode.SourceId}\u001f{entry.Episode.Route ?? string.Empty}";

    private static EpisodeIdentity CreateIdentity(SourceEpisodeEntry entry)
    {
        var title = entry.Episode.Title.Trim();
        var match = EpisodeNumberPattern.Match(title);
        if (match.Success
            && double.TryParse(
                match.Groups["number"].Value,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var number))
        {
            var displayNumber = number % 1 == 0
                ? number.ToString("0", CultureInfo.InvariantCulture)
                : number.ToString("0.##", CultureInfo.InvariantCulture);
            return new EpisodeIdentity(
                $"episode:{displayNumber}",
                $"第 {displayNumber} 集",
                number);
        }

        var key = string.Concat(title
            .Normalize()
            .Where(character =>
                !char.IsWhiteSpace(character)
                && !char.IsPunctuation(character)))
            .ToUpperInvariant();
        return new EpisodeIdentity(
            $"title:{key}",
            string.IsNullOrWhiteSpace(title) ? "未命名剧集" : title,
            double.MaxValue);
    }

    private static double GetHealthScore(
        SourceEpisodeEntry entry,
        IReadOnlyDictionary<string, RouteHealthRecord>? health)
    {
        if (health is null
            || !health.TryGetValue(GetRouteKey(entry), out var record))
        {
            return 0;
        }

        var successScore = record.SuccessCount * 4d;
        var failurePenalty = record.ConsecutiveFailures * 6d;
        var latencyPenalty = Math.Min(record.LastLatencyMilliseconds / 1000d, 8);
        return successScore - failurePenalty - latencyPenalty;
    }

    private sealed record EpisodeIdentity(
        string Key,
        string DisplayTitle,
        double SortOrder);
}
