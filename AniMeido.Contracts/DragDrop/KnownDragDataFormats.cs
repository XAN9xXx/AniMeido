namespace AniMeido.Contracts.DragDrop;

/// <summary>
/// AniMeido 拖拽系统已知数据格式常量。
/// 用于 DataPackage.SetData / GetData 时的格式名称标识。
/// </summary>
public static class KnownDragDataFormats
{
    /// <summary>番剧卡片拖拽格式标识。</summary>
    public const string AnimeCardKind = "AniMeido.AnimeCard";

    /// <summary>JSON 文本格式名，用于跨进程拖拽。</summary>
    public const string TextJson = "AniMeido.Json";
}
