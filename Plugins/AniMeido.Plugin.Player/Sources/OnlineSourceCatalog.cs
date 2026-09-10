using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Player.Sources.Managed;
using AniMeido.Plugin.Player.Sources.Rules;
using AniMeido.Plugin.Player.Sources.EasyBangumi;
using AniMeido.Plugin.Player.Sources.Subscriptions;
using AniMeido.Plugin.Player.Sources.Web;
using System.Collections.Concurrent;
using System.Net.Http;

namespace AniMeido.Plugin.Player.Sources;

/// <summary>
/// Aggregates online source providers while isolating individual source failures.
/// </summary>
internal sealed class OnlineSourceCatalog
{
    private readonly Func<IEnumerable<IOnlineAnimeSource>>? _sourceFactory;
    private readonly SourceMappingStore _mappingStore;
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private readonly object _diagnosticsGate = new();
    private IReadOnlyDictionary<string, IOnlineAnimeSource> _sources;
    private IReadOnlyList<SourceDiagnostic> _lastDiagnostics = [];
    private IReadOnlyList<SourceMappingRequest> _lastMappingRequests = [];

    public OnlineSourceCatalog(
        HttpClient httpClient,
        SourceMappingStore mappingStore,
        WebMediaResolver webResolver,
        EasyPreferenceStore preferenceStore)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(mappingStore);
        ArgumentNullException.ThrowIfNull(webResolver);
        ArgumentNullException.ThrowIfNull(preferenceStore);
        var managedSources = ManagedSourceLoader.Load().ToArray();
        _sourceFactory = () => CreateSources(
            httpClient,
            webResolver,
            preferenceStore,
            managedSources);
        _sources = CreateSourceMap(_sourceFactory());
        _mappingStore = mappingStore;
    }

    private static IEnumerable<IOnlineAnimeSource> CreateSources(
        HttpClient httpClient,
        WebMediaResolver webResolver,
        EasyPreferenceStore preferenceStore,
        IReadOnlyList<IOnlineAnimeSource> managedSources)
    {
        foreach (var source in SourceRuleLoader.Load(httpClient))
        {
            yield return source;
        }

        foreach (var source in managedSources)
        {
            yield return source;
        }

        foreach (var source in SubscriptionSourceLoader.Load(
            httpClient,
            webResolver,
            preferenceStore))
        {
            yield return source;
        }
    }

    internal OnlineSourceCatalog(
        IEnumerable<IOnlineAnimeSource> sources,
        SourceMappingStore? mappingStore = null)
    {
        _sources = CreateSourceMap(sources);
        _mappingStore = mappingStore ?? new SourceMappingStore(path: null);
    }

    internal OnlineSourceCatalog(
        Func<IEnumerable<IOnlineAnimeSource>> sourceFactory,
        SourceMappingStore? mappingStore = null)
    {
        ArgumentNullException.ThrowIfNull(sourceFactory);
        _sourceFactory = sourceFactory;
        _sources = CreateSourceMap(sourceFactory());
        _mappingStore = mappingStore ?? new SourceMappingStore(path: null);
    }

    internal IReadOnlyList<SourceMappingRequest> LastMappingRequests
    {
        get
        {
            lock (_diagnosticsGate)
            {
                return _lastMappingRequests;
            }
        }
    }

    public int SourceCount => Volatile.Read(ref _sources).Count;

    public IReadOnlyList<SourceDiagnostic> LastDiagnostics
    {
        get
        {
            lock (_diagnosticsGate)
            {
                return _lastDiagnostics;
            }
        }
    }

    public async Task<IReadOnlyList<SourceEpisodeEntry>> GetEpisodesAsync(
        AnimePlaybackContext anime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(anime);
        var sources = Volatile.Read(ref _sources);
        var entries = new ConcurrentBag<SourceEpisodeEntry>();
        var diagnostics = new ConcurrentBag<SourceDiagnostic>();
        var mappingRequests = new ConcurrentBag<SourceMappingRequest>();
        using var concurrencyGate = new SemaphoreSlim(4, 4);
        var tasks = sources.Values.Select(async source =>
        {
            await concurrencyGate.WaitAsync(cancellationToken);
            try
            {
                using var sourceCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                var timeout = source is IMappableOnlineAnimeSource mappableSource
                    ? mappableSource.SearchTimeout
                    : TimeSpan.FromSeconds(15);
                sourceCancellation.CancelAfter(timeout);
                try
                {
                    var episodes = source is IMappableOnlineAnimeSource mappable
                        ? await GetMappedEpisodesAsync(
                            anime,
                            mappable,
                            mappingRequests,
                            sourceCancellation.Token)
                        : await source.GetEpisodesAsync(
                            anime,
                            sourceCancellation.Token);
                    foreach (var episode in episodes)
                    {
                        entries.Add(new SourceEpisodeEntry(
                            source.DisplayName,
                            episode));
                    }
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    diagnostics.Add(new SourceDiagnostic(
                        source.Id,
                        source.DisplayName,
                        "查找剧集",
                        timeout > TimeSpan.FromSeconds(15)
                            ? $"查找或人工验证超过 {timeout.TotalMinutes:0} 分钟，已跳过。"
                            : "请求超过 15 秒，已跳过。"));
                }
#pragma warning disable CA1031 // One source must not hide episodes from other sources.
                catch (Exception ex)
                {
                    diagnostics.Add(new SourceDiagnostic(
                        source.Id,
                        source.DisplayName,
                        "查找剧集",
                        ex.Message));
                }
#pragma warning restore CA1031
            }
            finally
            {
                concurrencyGate.Release();
            }
        });
        await Task.WhenAll(tasks);

        lock (_diagnosticsGate)
        {
            _lastDiagnostics = diagnostics.ToArray();
            _lastMappingRequests = mappingRequests
                .OrderBy(request => request.SourceName)
                .ToArray();
        }

        return entries
            .OrderBy(entry => GetTier(sources, entry.Episode.SourceId))
            .ThenBy(entry => entry.SourceName, StringComparer.CurrentCulture)
            .ThenBy(entry => entry.Episode.Title, StringComparer.CurrentCulture)
            .ToArray();
    }

    internal async Task<IReadOnlyList<SourceEpisodeEntry>>
        SelectMappingAsync(
            AnimePlaybackContext anime,
            SourceAnimeCandidate candidate,
            CancellationToken cancellationToken)
    {
        var sources = Volatile.Read(ref _sources);
        if (!sources.TryGetValue(
                candidate.SourceId,
                out var source)
            || source is not IMappableOnlineAnimeSource mappable)
        {
            throw new InvalidOperationException(
                $"播放源不支持标题映射：{candidate.SourceId}");
        }

        await _mappingStore.SetAsync(
            anime.AnimeId,
            source.Id,
            candidate.RemoteId,
            cancellationToken);
        var episodes = await mappable.GetEpisodesAsync(
            candidate,
            cancellationToken);
        return episodes
            .Select(episode => new SourceEpisodeEntry(
                source.DisplayName,
                episode))
            .ToArray();
    }

    public async Task<ResolvedMedia> ResolveAsync(
        SourceEpisode episode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(episode);
        var sources = Volatile.Read(ref _sources);
        if (!sources.TryGetValue(episode.SourceId, out var source))
        {
            throw new InvalidOperationException(
                $"播放源未安装或已停用：{episode.SourceId}");
        }

        try
        {
            return await source.ResolveAsync(
                episode,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SourceResolutionException ex)
        {
            AppendDiagnostic(new SourceDiagnostic(
                source.Id,
                source.DisplayName,
                "解析媒体",
                HeaderNormalizer.RedactDiagnostic(ex.Message)));
            throw;
        }
#pragma warning disable CA1031 // Source failures are normalized for the player UI.
        catch (Exception ex)
        {
            AppendDiagnostic(new SourceDiagnostic(
                source.Id,
                source.DisplayName,
                "解析媒体",
                HeaderNormalizer.RedactDiagnostic(ex.Message)));
            throw new InvalidOperationException(
                $"{source.DisplayName} 解析失败："
                + HeaderNormalizer.RedactDiagnostic(ex.Message),
                ex);
        }
#pragma warning restore CA1031
    }

    private void AppendDiagnostic(SourceDiagnostic diagnostic)
    {
        lock (_diagnosticsGate)
        {
            _lastDiagnostics = _lastDiagnostics
                .Append(diagnostic)
                .TakeLast(20)
                .ToArray();
        }
    }

    private async Task<IReadOnlyList<SourceEpisode>> GetMappedEpisodesAsync(
        AnimePlaybackContext anime,
        IMappableOnlineAnimeSource source,
        ConcurrentBag<SourceMappingRequest> mappingRequests,
        CancellationToken cancellationToken)
    {
        var candidates = await source.SearchAsync(anime, cancellationToken);
        var rememberedId = await _mappingStore.GetAsync(
            anime.AnimeId,
            source.Id,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(rememberedId))
        {
            var remembered = candidates.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.RemoteId,
                    rememberedId,
                    StringComparison.Ordinal));
            if (remembered is not null)
            {
                return await source.GetEpisodesAsync(
                    remembered,
                    cancellationToken);
            }

            await _mappingStore.RemoveAsync(
                anime.AnimeId,
                source.Id,
                cancellationToken);
        }

        var ranked = TitleMatcher.Rank(anime, candidates);
        if (ranked.Count == 0)
        {
            return [];
        }

        if (ranked.Count == 1 || TitleMatcher.IsConfident(anime, ranked[0]))
        {
            await _mappingStore.SetAsync(
                anime.AnimeId,
                source.Id,
                ranked[0].RemoteId,
                cancellationToken);
            return await source.GetEpisodesAsync(
                ranked[0],
                cancellationToken);
        }

        mappingRequests.Add(new SourceMappingRequest(
            source.Id,
            source.DisplayName,
            ranked.Take(10).ToArray()));
        return [];
    }

    public async Task<SourceCatalogReloadResult> ReloadAsync(
        CancellationToken cancellationToken)
    {
        if (_sourceFactory is null)
        {
            var unchanged = Volatile.Read(ref _sources).Count;
            return new SourceCatalogReloadResult(unchanged, unchanged);
        }

        await _reloadGate.WaitAsync(cancellationToken);
        try
        {
            var previous = Volatile.Read(ref _sources);
            var next = await Task.Run(
                () => CreateSourceMap(_sourceFactory()),
                cancellationToken);
            Volatile.Write(ref _sources, next);
            return new SourceCatalogReloadResult(previous.Count, next.Count);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private static IReadOnlyDictionary<string, IOnlineAnimeSource>
        CreateSourceMap(IEnumerable<IOnlineAnimeSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var sourceMap = new Dictionary<string, IOnlineAnimeSource>(
            StringComparer.Ordinal);
        foreach (var source in sources)
        {
            sourceMap.TryAdd(source.Id, source);
        }

        return sourceMap;
    }

    private static int GetTier(
        IReadOnlyDictionary<string, IOnlineAnimeSource> sources,
        string sourceId)
        => sources.TryGetValue(sourceId, out var source)
            && source is ISourceTier ranked
                ? ranked.Tier
                : 0;
}

internal sealed record SourceCatalogReloadResult(
    int PreviousCount,
    int CurrentCount);
