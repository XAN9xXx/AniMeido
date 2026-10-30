using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using System.Runtime.CompilerServices;

namespace AniMeido.Plugin.Base.Services;

internal readonly record struct MetadataCacheStamp(string? DataVersion, long Generation);

/// <summary>派生缓存与资料版本、用户清缓存操作使用同一有效范围。</summary>
internal interface IMetadataCacheScope
{
    MetadataCacheStamp CacheStamp { get; }
}

/// <summary>
/// 每个数据源只保留一天、一个资料版本的主题缓存。
/// 进行中的旧请求持有旧 scope，完成后不会把结果写回当前 scope。
/// </summary>
internal sealed class TodayThemeCache
{
    private static readonly ConditionalWeakTable<IAnimeDataSource, TodayThemeCache> Sources = new();
    private readonly object _gate = new();
    private Scope? _current;

    internal static Scope For(IAnimeDataSource source, DateOnly date)
    {
        var cache = Sources.GetValue(source, static _ => new TodayThemeCache());
        var stamp = (source as IMetadataCacheScope)?.CacheStamp ?? default;
        lock (cache._gate)
        {
            if (cache._current is not { } current || current.Date != date || current.Stamp != stamp)
                cache._current = new Scope(date, stamp);

            return cache._current;
        }
    }

    internal static void ResetSession() => Sources.Clear();

    internal sealed class Scope(DateOnly date, MetadataCacheStamp stamp)
    {
        private readonly object _gate = new();
        private readonly Dictionary<TodayThemeKind, TodayThemeContent> _content = [];
        private readonly Dictionary<string, IReadOnlyList<Anime>> _genres = new(StringComparer.OrdinalIgnoreCase);

        internal DateOnly Date { get; } = date;
        internal MetadataCacheStamp Stamp { get; } = stamp;

        internal TodayThemeContent? FindContent(TodayThemeKind kind)
        {
            lock (_gate)
                return _content.GetValueOrDefault(kind);
        }

        internal void StoreContent(TodayThemeKind kind, TodayThemeContent content)
        {
            lock (_gate)
                _content[kind] = content;
        }

        internal IReadOnlyList<Anime>? FindGenre(string genre)
        {
            lock (_gate)
                return _genres.GetValueOrDefault(genre);
        }

        internal void StoreGenre(string genre, IReadOnlyList<Anime> items)
        {
            lock (_gate)
                _genres[genre] = items;
        }
    }
}
