namespace AniMeido.Plugin.Base.Models;

/// <summary>基础资料、截图登记及标签来自同一个 SQLite 读事务。</summary>
internal sealed record ArchiveBundleSnapshot(
    string DataJson,
    IReadOnlyList<AnimeScreenshot> Screenshots,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Tags);
