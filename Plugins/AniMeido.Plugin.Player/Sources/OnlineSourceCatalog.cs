using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Player.Sources.Managed;
using AniMeido.Plugin.Player.Sources.Rules;
using AniMeido.Plugin.Player.Sources.EasyBangumi;
using AniMeido.Plugin.Player.Sources.Subscriptions;
using AniMeido.Plugin.Player.Sources.Web;
using System.Collections.Concurrent;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace AniMeido.Plugin.Player.Sources;

/// <summary>
/// Aggregates online source providers while isolating individual source failures.
/// </summary>
internal sealed class OnlineSourceCatalog
{
    private readonly Func<IEnumerable<IOnlineAnimeSource>>? _sourceFactory;
    private readonly SourceMappingStore _mappingStore;
    private readonly ILogger<OnlineSourceCatalog>? _logger;
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private readonly object _diagnosticsGate = new();
    private IReadOnlyDictionary<string, IOnlineAnimeSource>? _sources;
    private IReadOnlyList<SourceDiagnostic> _lastDiagnostics = [];
    private IReadOnlyList<SourceMappingRequest> _lastMappingRequests = [];

    public OnlineSourceCatalog(
        HttpClient httpClient,
        SourceMappingStore mappingStore,
        WebMediaResolver webResolver,
        EasyPreferenceStore preferenceStore,
        ILogger<OnlineSourceCatalog>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(mappingStore);
        ArgumentNullException.ThrowIfNull(webResolver);
        ArgumentNullException.ThrowIfNull(preferenceStore);
        var managedSources = new Lazy<IReadOnlyList<IOnlineAnimeSource>>(
            () => ManagedSourceLoader.Load(logger).ToArray());
        _sourceFactory = () => CreateSources(
            httpClient,
            webResolver,
            preferenceStore,
            managedSources.Value,
            logger);
        _mappingStore = mappingStore;
        _logger = logger;
    }

    private static IEnumerable<IOnlineAnimeSource> CreateSources(
        HttpClient httpClient,
        WebMediaResolver webResolver,
        EasyPreferenceStore preferenceStore,
        IReadOnlyList<IOnlineAnimeSource> managedSources,
        ILogger? logger)
    {
        foreach (var source in SourceRuleLoader.Load(httpClient, logger))
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
            preferenceStore,
            logger))
        {
            yield return source;
        }
    }

    internal OnlineSourceCatalog(
        IEnumerable<IOnlineAnimeSource> sources,
        SourceMappingStore? mappingStore = null,
        ILogger<OnlineSourceCatalog>? logger = null)
    {
        _sources = CreateSourceMap(sources);
        _mappingStore = mappingStore ?? new SourceMappingStore(path: null);
        _logger = logger;
    }

    internal OnlineSourceCatalog(
        Func<IEnumerable<IOnlineAnimeSource>> sourceFactory,
        SourceMappingStore? mappingStore = null,
        ILogger<OnlineSourceCatalog>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sourceFactory);
        _sourceFactory = sourceFactory;
        _mappingStore = mappingStore ?? new SourceMappingStore(path: null);
        _logger = logger;
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

    internal SourceMappingStore MappingStore => _mappingStore;

    public int SourceCount => Volatile.Read(ref _sources)?.Count ?? 0;

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
        CancellationToken cancellationToken,
        SourceMappingLookup? preparedLookup = null)
    {
        ArgumentNullException.ThrowIfNull(anime);
        var lookup = preparedLookup ?? await _mappingStore.BeginLookupAsync(anime.AnimeId, cancellationToken);
        anime = anime with { AnimeId = lookup.AnimeId };
        var sources = await GetSourcesAsync(cancellationToken);
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
                            lookup,
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
                catch (OperationCanceledException ex)
                {
                    _logger?.LogWarning(ex, "Source {SourceId} episode lookup timed out; skipping source", source.Id);
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
                    cancellationToken.ThrowIfCancellationRequested();
                    _logger?.LogWarning(ex, "Source {SourceId} episode lookup failed; skipping source", source.Id);
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
            cancellationToken.ThrowIfCancellationRequested();
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
            SourceMappingRequest request,
            SourceAnimeCandidate candidate,
            CancellationToken cancellationToken)
    {
        var sources = await GetSourcesAsync(cancellationToken);
        if (!sources.TryGetValue(
                candidate.SourceId,
                out var source)
            || source is not IMappableOnlineAnimeSource mappable)
        {
            throw new InvalidOperationException(
                $"播放源不支持标题映射：{candidate.SourceId}");
        }

        await request.MappingLookup.SetAsync(
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
        var sources = await GetSourcesAsync(cancellationToken);
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
                HeaderNormalizer.RedactDiagnostic(ex.Message)), cancellationToken);
            throw;
        }
#pragma warning disable CA1031 // Source failures are normalized for the player UI.
        catch (Exception ex)
        {
            AppendDiagnostic(new SourceDiagnostic(
                source.Id,
                source.DisplayName,
                "解析媒体",
                HeaderNormalizer.RedactDiagnostic(ex.Message)), cancellationToken);
            throw new InvalidOperationException(
                $"{source.DisplayName} 解析失败："
                + HeaderNormalizer.RedactDiagnostic(ex.Message),
                ex);
        }
#pragma warning restore CA1031
    }

    private void AppendDiagnostic(SourceDiagnostic diagnostic, CancellationToken cancellationToken)
    {
        lock (_diagnosticsGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _lastDiagnostics = _lastDiagnostics
                .Append(diagnostic)
                .TakeLast(20)
                .ToArray();
        }
    }

    private async Task<IReadOnlyList<SourceEpisode>> GetMappedEpisodesAsync(
        AnimePlaybackContext anime,
        IMappableOnlineAnimeSource source,
        SourceMappingLookup lookup,
        ConcurrentBag<SourceMappingRequest> mappingRequests,
        CancellationToken cancellationToken)
    {
        var candidates = await source.SearchAsync(anime, cancellationToken);
        var rememberedId = await lookup.GetAsync(
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

            await lookup.RemoveAsync(
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
            await lookup.SetAsync(
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
            ranked.Take(10).ToArray())
        {
            MappingLookup = lookup,
        });
        return [];
    }

    internal Task EnsureLoadedAsync(CancellationToken cancellationToken)
        => LoadSourcesAsync(forceReload: false, cancellationToken);

    private async Task<IReadOnlyDictionary<string, IOnlineAnimeSource>> GetSourcesAsync(
        CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return Volatile.Read(ref _sources)!;
    }

    public Task<SourceCatalogReloadResult> ReloadAsync(CancellationToken cancellationToken)
        => LoadSourcesAsync(forceReload: true, cancellationToken);

    private async Task<SourceCatalogReloadResult> LoadSourcesAsync(
        bool forceReload,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = Volatile.Read(ref _sources);
        if (_sourceFactory is null || (!forceReload && current is not null))
        {
            var unchanged = current?.Count ?? 0;
            return new SourceCatalogReloadResult(unchanged, unchanged);
        }

        await _reloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = Volatile.Read(ref _sources);
            if (!forceReload && previous is not null)
            {
                return new SourceCatalogReloadResult(previous.Count, previous.Count);
            }

            var next = await Task.Run(
                () => CreateSourceMap(_sourceFactory()),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref _sources, next);
            return new SourceCatalogReloadResult(previous?.Count ?? 0, next.Count);
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
