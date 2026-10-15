namespace AniMeido.Contracts.DragDrop;

/// <summary>
/// 拖拽载荷相关的标识常量。载荷本身经 StandardDataFormats.Text 传递。
/// </summary>
public static class KnownDragDataFormats
{
    /// <summary>载荷 kind 字段的取值；反序列化时按 Ordinal 比较。</summary>
    public const string AnimeCardKind = "AniMeido.AnimeCard";

    /// <summary>JSON 文本格式名，用于跨进程拖拽。</summary>
    public const string TextJson = "AniMeido.Json";
}
