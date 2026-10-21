using AniMeido.Contracts.Models;
using System.Globalization;

namespace AniMeido.Plugin.Base.Services;

public sealed partial class MetadataDataSource
{
    private static readonly HashSet<string> StudioRoles =
        ["production", "original-creator", "planning", "animation-production", "distribution"];

    private static string ImageUrl(string entity, int id, string usage, bool large) =>
        $"{MetadataServiceSettings.ImageBaseUrl}/v1/images/{entity}/{id}/{usage}?size={(large ? "large" : "small")}";

    private static string Title(IEnumerable<MetadataName> names)
    {
        var main = names.Where(n => n.Kind == "main" && !string.IsNullOrWhiteSpace(n.Text)).ToList();
        return main.FirstOrDefault(n => n.Lang == "zh-Hans")?.Text
            ?? main.FirstOrDefault(n => n.Lang == "ja")?.Text
            ?? main.FirstOrDefault()?.Text
            ?? "不好，标题走丢了Q^Q";
    }

    private static string EntityName(Dictionary<string, string> names) =>
        Title(names.Select(n => new MetadataName("main", n.Key, n.Value)));

    private static DateOnly? FullDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date : null;

    internal static DateOnly? StartDate(string? value) => value?.Length switch
    {
        10 => FullDate(value),
        7 => FullDate(value + "-01"),
        4 => FullDate(value + "-01-01"),
        _ => null,
    };

    private static (int Year, int Month) SeasonDate(string? value)
    {
        if (value is { Length: >= 7 }
            && DateOnly.TryParseExact(value[..7] + "-01", "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return (date.Year, (date.Month - 1) / 3 * 3 + 1);
        return (0, 0);
    }

    internal static AnimeMediaFormat MapMediaFormat(string? format) => format switch
    {
        "tv" => AnimeMediaFormat.Television,
        "ova" => AnimeMediaFormat.Ova,
        "movie" => AnimeMediaFormat.Movie,
        "web" => AnimeMediaFormat.Ona,
        _ => AnimeMediaFormat.Unknown,
    };

    private static MetadataRating? BangumiRating(IEnumerable<MetadataRating> ratings) =>
        ratings.FirstOrDefault(r => r.Platform == "bangumi");

    private static List<Studio> Studios(MetadataWork work) => work.Staff
        .Where(s => StudioRoles.Contains(s.Role))
        .SelectMany(s => s.Participants)
        .Where(p => p.Id > 0)
        .DistinctBy(p => p.Id)
        .Select(p => new Studio(p.Id, EntityName(p.MainNames), ImageUrl("participant", p.Id, "avatar", true)))
        .ToList();

    private static IEnumerable<MetadataCharacter> Characters(MetadataWork work) => work.Characters
        .OrderBy(c => c.Role switch { "main" => 0, "supporting" => 1, _ => 2 });

    private static VoiceActor VoiceActor(MetadataEntity actor) =>
        new(actor.Id, EntityName(actor.MainNames), ImageUrl("participant", actor.Id, "avatar", true));

    private static List<VoiceActor> VoiceActors(MetadataWork work) => Characters(work)
        .SelectMany(c => c.Cv).Where(p => p.Id > 0).DistinctBy(p => p.Id).Select(VoiceActor).ToList();

    private Anime MapAnime(MetadataWork work, bool detail = false)
    {
        var title = Title(work.Names);
        var (year, month) = SeasonDate(work.FirstDate);
        var studios = detail ? Studios(work) : [];
        var description = new[] { "zh-Hans", "ja", "en" }
            .Select(lang => work.Summaries.FirstOrDefault(s => s.Lang == lang && !string.IsNullOrWhiteSpace(s.Text))?.Text)
            .FirstOrDefault(text => text is not null) ?? "";
        return new Anime(
            work.Id, title,
            studios.Count > 0 ? string.Join("、", studios.Select(s => s.Name)) : null,
            detail ? VoiceActors(work) : [],
            FullDate(work.FirstDate), ImageUrl("work", work.Id, "cover", detail), description,
            year, month, work.BroadcastWeekday, CompositeRating.Calculate(work.Ratings, work.FirstDate, Today)?.Score,
            work.Names.Where(n => n.Kind is "main" or "alias" && !string.IsNullOrWhiteSpace(n.Text))
                .Select(n => n.Text.Trim()).Where(t => !string.Equals(t, title, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            MapMediaFormat(work.Format));
    }

    private static List<(string Category, string Code)> TagCodes(MetadataWork work)
    {
        var codes = work.Tags.Select(t => ("tag", t)).ToList();
        if (work.Format is not null) codes.Add(("format", work.Format));
        if (work.SourceType is not null) codes.Add(("source_type", work.SourceType));
        codes.AddRange(work.Countries.Select(c => ("country", c)));
        return codes;
    }

    private static string TagParameter(string category) => category switch
    {
        "source_type" => "sourceType",
        _ => category,
    };
}
