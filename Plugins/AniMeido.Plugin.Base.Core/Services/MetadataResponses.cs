using System.Text.Json;
using System.Text.Json.Serialization;

namespace AniMeido.Plugin.Base.Services;

internal class MetadataResponse
{
    public string DataVersion { get; set; } = "";
}

internal sealed class MetadataPage<T> : MetadataResponse
{
    public int Total { get; set; }
    public List<T> Items { get; set; } = [];
}

internal sealed class MetadataDetail : MetadataResponse
{
    public MetadataWork? Data { get; set; }
    public MetadataProblem? Problem { get; set; }
}

internal sealed class MetadataWork
{
    public int Id { get; set; }
    public string? Format { get; set; }
    public string? SourceType { get; set; }
    public string? FirstDate { get; set; }
    public int? BroadcastWeekday { get; set; }
    public string? Status { get; set; }
    public List<MetadataName> Names { get; set; } = [];
    public List<MetadataText> Summaries { get; set; } = [];
    public List<string> Countries { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public List<MetadataRating> Ratings { get; set; } = [];
    public List<MetadataStaff> Staff { get; set; } = [];
    public List<MetadataCharacter> Characters { get; set; } = [];
}

internal sealed record MetadataName(string Kind, string? Lang, string Text);
internal sealed record MetadataText(string Lang, string Text);
internal sealed record MetadataRating(string Platform, double? Score,
    [property: JsonConverter(typeof(MetadataVotesConverter))] long? Votes, string? FetchedOn = null);

internal sealed class MetadataVotesConverter : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // 单站人数异常不应使整份作品资料失效；保留缺失语义，由评分规则排除该条。
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetDecimal(out var votes)
            && votes == decimal.Truncate(votes) && votes >= long.MinValue && votes <= long.MaxValue)
            return (long)votes;
        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value is long votes) writer.WriteNumberValue(votes);
        else writer.WriteNullValue();
    }
}
internal sealed record MetadataEntity(int Id, Dictionary<string, string> MainNames);
internal sealed record MetadataStaff(string Role, List<MetadataEntity> Participants);
internal sealed record MetadataCharacter(MetadataEntity Character, string Role, List<MetadataEntity> Cv);
internal sealed record MetadataVocab(string Category, string Code, string NameZh);
internal sealed record MetadataSearchItem(int Id);
internal sealed record MetadataPersonWork(MetadataBrief Work, MetadataEntity? Character, string? Role);

internal sealed class MetadataBrief
{
    public int Id { get; set; }
    public Dictionary<string, string> MainNames { get; set; } = [];
    public string? FirstDate { get; set; }
    public List<MetadataRating> Ratings { get; set; } = [];
}

internal sealed record MetadataProblem(string? Code, int? MergedInto);

internal sealed record MetadataRatingDetails(
    IReadOnlyList<MetadataRating> Ratings,
    string? FirstDate,
    DateOnly AsOf);
