using System.Text.Json.Serialization;

namespace AniMeido.Contracts.DragDrop;

/// <summary>
/// 番剧卡片拖拽载荷 — 拖拽系统的统一数据事实。
///
/// == 定位 ==
/// AnimeCardDragPayload 是 AniMeido 跨区域拖拽的数据事实来源。
/// AnimeCard 标准拖拽使用此格式。
/// 不包含 UI 类型（ImageSource、Brush、控件引用等），纯数据对象。
///
/// == 传递方式 ==
/// 序列化为 JSON → StandardDataFormats.Text → DragEventArgs.DataView。
/// 接收方通过 AnimeCardDragPayloadSerializer.Deserialize 还原。
///
/// == 系统分层 ==
/// Drag Source: AnimeCard（OnBodyDragStarting）
/// Shell 层: AnimeCardDropHost（主窗口兜底接收）
/// 页面层: DragDropService（Zone 构建、路由、标记状态写入）
/// 自定义拖拽视觉属于纯视觉层，不参与数据传递
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
