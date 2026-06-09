using System.Text.Json.Serialization;

namespace AniMeido.Contracts.DragDrop;

/// <summary>
/// 番剧卡片拖拽载荷。包含可序列化的番剧摘要信息，不包含 UI 类型。
///
/// 跨插件边界（BasePlugin → ChatPlugin 等）传递拖拽数据时应使用此对象，
/// 而非直接传递 Anime 模型或控件引用。
/// </summary>
public sealed class AnimeCardDragPayload
{
    /// <summary>载荷类型标识，固定为 "AniMeido.AnimeCard"。</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = KnownDragDataFormats.AnimeCardKind;

    /// <summary>结构版本号，用于兼容性判断。</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    /// <summary>番剧 ID（Bangumi ID）。</summary>
    [JsonPropertyName("animeId")]
    public int AnimeId { get; init; }

    /// <summary>番剧标题。</summary>
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    /// <summary>封面图片 URL。</summary>
    [JsonPropertyName("coverImageUrl")]
    public string? CoverImageUrl { get; init; }

    /// <summary>简介摘要。</summary>
    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    /// <summary>季度年份。</summary>
    [JsonPropertyName("seasonYear")]
    public int SeasonYear { get; init; }

    /// <summary>季度月份（1=冬, 4=春, 7=夏, 10=秋）。</summary>
    [JsonPropertyName("seasonMonth")]
    public int SeasonMonth { get; init; }

    /// <summary>来源页面标识，如 "CurrentSeason"、"Search"、"TagResult"。</summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }
}
