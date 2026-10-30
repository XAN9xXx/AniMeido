namespace AniMeido.Contracts.DragDrop;

/// <summary>
/// 拖拽载荷相关的标识常量。载荷本身经 StandardDataFormats.Text 传递。
/// </summary>
public static class KnownDragDataFormats
{
    /// <summary>载荷 kind 字段的取值；反序列化时按 Ordinal 比较。</summary>
    public const string AnimeCardKind = "AniMeido.AnimeCard";

    /// <summary>保留的扩展格式标识；内置拖拽使用标准 Text，不主动写入此格式。</summary>
    public const string TextJson = "AniMeido.Json";
}
