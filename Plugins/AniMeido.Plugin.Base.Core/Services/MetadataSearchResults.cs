using AniMeido.Contracts.Models;
using System.Globalization;

namespace AniMeido.Plugin.Base.Services;

internal sealed record SearchContext(string DataVersion, string SearchVersion, string AsOf);

internal sealed class MetadataSearchPage : MetadataResponse
{
    public string SearchVersion { get; set; } = "";
    public string AsOf { get; set; } = "";
    public int Total { get; set; }
    public bool Complete { get; set; }
    public List<MetadataSearchEntry> Items { get; set; } = [];
    public DateTimeOffset FetchedAt { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsOffline { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public SearchContext Context => new(DataVersion, SearchVersion, AsOf);
}

internal sealed class MetadataSearchEntry
{
    public int Id { get; set; }
    public Dictionary<string, string> MainNames { get; set; } = [];
    public string MatchedName { get; set; } = "";
    public string? MatchedNameKind { get; set; }
    public int RelevanceOrder { get; set; }
    public string? FirstDate { get; set; }
    public string? Format { get; set; }
    public List<MetadataRating> Ratings { get; set; } = [];
}

internal sealed class MetadataCapabilities : MetadataResponse
{
    public MetadataVersionData? Data { get; set; }
}
internal sealed record MetadataVersionData(MetadataSearchCapability? Search);
internal sealed record MetadataSearchCapability(string SearchVersion, bool CompleteWorkSort);

internal static class MetadataCalendar
{
    internal static DateOnly Today(TimeProvider time) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(
        time.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai")).DateTime);
    internal static string Date(TimeProvider time) => Today(time).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

internal sealed record KeywordSearchPage(MetadataSearchPage Raw, IReadOnlyList<Anime> Items);
