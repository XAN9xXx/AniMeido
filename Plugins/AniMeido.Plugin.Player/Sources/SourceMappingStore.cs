using System.Text.Json;
using AniMeido.Plugin.Player.Playback;

namespace AniMeido.Plugin.Player.Sources;

internal sealed class SourceMappingStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };
    private readonly string? _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, string>? _mappings;
    private readonly string? _migrationPath;
    private int _idMigrationVersion;
    private readonly string? _remapPath;
    private long _lastRemapSeq;
    private readonly List<WeakReference<SourceMappingLookup>> _lookups = [];

    public SourceMappingStore()
        : this(Path.Combine(
            Packages.SourcePackageInstaller.GetSourcesDirectory(),
            "Mappings",
            "mappings.json"), PlayerPublicIds.SharedMigrationPath, PlayerPublicIds.SharedRemapPath)
    {
    }

    internal SourceMappingStore(string? path, string? migrationPath = null, string? remapPath = null)
    {
        _path = path;
        _migrationPath = migrationPath;
        _remapPath = remapPath;
    }

    internal Task EnsurePublicIdsAsync(CancellationToken ct)
        => WithGateAsync(async token =>
        {
            await ApplyPublicIdsAsync(null, token);
            return true;
        }, ct);

    internal Task EnsurePublicIdsAsync(PlayerPublicIdChanges changes, CancellationToken ct)
        => WithGateAsync(async token =>
        {
            await ApplyPublicIdsAsync(null, token, changes);
            return true;
        }, ct);

    internal Task<SourceMappingLookup> BeginLookupAsync(int animeId, CancellationToken ct)
        => WithGateAsync(token => BeginLookupCoreAsync(animeId, null, token), ct);

    internal Task<SourceMappingLookup> BeginLookupAsync(
        int animeId, PlayerPublicIdChanges changes, CancellationToken ct)
        => WithGateAsync(token => BeginLookupCoreAsync(animeId, changes, token), ct);

    private async Task<SourceMappingLookup> BeginLookupCoreAsync(
        int animeId, PlayerPublicIdChanges? changes, CancellationToken ct)
    {
        var currentAnimeId = await ApplyPublicIdsAsync(animeId, ct, changes) ?? animeId;
        var lookup = new SourceMappingLookup(this, currentAnimeId, ReadAnimeMappings(_mappings!, currentAnimeId));
        RemoveExpiredLookups();
        _lookups.Add(new WeakReference<SourceMappingLookup>(lookup));
        return lookup;
    }

    internal Task<string?> GetAsync(SourceMappingLookup lookup, string sourceId, CancellationToken ct)
        => WithGateAsync(_ => Task.FromResult(lookup.Values.GetValueOrDefault(sourceId)), ct);

    internal Task SetAsync(SourceMappingLookup lookup, string sourceId, string remoteId, CancellationToken ct)
        => WithGateAsync(async token =>
        {
            if (!lookup.ShadowedSources.Contains(sourceId))
            {
                var updated = new Dictionary<string, string>(_mappings!, StringComparer.Ordinal)
                {
                    [GetKey(lookup.CurrentAnimeId, sourceId)] = remoteId,
                };
                await SaveValuesAsync(updated, _idMigrationVersion, token);
                _mappings = updated;
            }
            lookup.Values[sourceId] = remoteId;
            return true;
        }, ct);

    internal Task RemoveAsync(SourceMappingLookup lookup, string sourceId, CancellationToken ct)
        => WithGateAsync(async token =>
        {
            var key = GetKey(lookup.CurrentAnimeId, sourceId);
            if (!lookup.ShadowedSources.Contains(sourceId) && _mappings!.ContainsKey(key))
            {
                var updated = new Dictionary<string, string>(_mappings!, StringComparer.Ordinal);
                updated.Remove(key);
                await SaveValuesAsync(updated, _idMigrationVersion, token);
                _mappings = updated;
            }
            lookup.Values.Remove(sourceId);
            return true;
        }, ct);

    public Task<string?> GetAsync(
        int animeId,
        string sourceId,
        CancellationToken cancellationToken)
        => WithGateAsync(async ct =>
        {
            await EnsureLocalLoadedAsync(ct);
            return _mappings!.GetValueOrDefault(GetKey(animeId, sourceId));
        }, cancellationToken);

    public Task SetAsync(
        int animeId,
        string sourceId,
        string remoteId,
        CancellationToken cancellationToken)
        => WithGateAsync(async ct =>
        {
            await EnsureLocalLoadedAsync(ct);
            var updated = new Dictionary<string, string>(_mappings!, StringComparer.Ordinal)
            {
                [GetKey(animeId, sourceId)] = remoteId,
            };
            await SaveValuesAsync(updated, _idMigrationVersion, ct);
            _mappings = updated;
            return true;
        }, cancellationToken);

    public Task RemoveAsync(
        int animeId,
        string sourceId,
        CancellationToken cancellationToken)
        => WithGateAsync(async ct =>
        {
            await EnsureLocalLoadedAsync(ct);
            var key = GetKey(animeId, sourceId);
            if (!_mappings!.ContainsKey(key))
                return false;

            var updated = new Dictionary<string, string>(_mappings!, StringComparer.Ordinal);
            updated.Remove(key);
            await SaveValuesAsync(updated, _idMigrationVersion, ct);
            _mappings = updated;
            return true;
        }, cancellationToken);

    private async Task<T> WithGateAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        // 先排队再调度，整次映射操作不依赖 UI 继续处理消息。
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => action(ct), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLocalLoadedAsync(CancellationToken cancellationToken)
    {
        if (_mappings is null && (_path is null || !File.Exists(_path)))
        {
            _mappings = new Dictionary<string, string>(StringComparer.Ordinal);
        }
        if (_mappings is null)
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(_path!, cancellationToken));
            if (doc.RootElement.TryGetProperty("Mappings", out _))
            {
                var envelope = doc.RootElement.Deserialize<MappingFile>(SerializerOptions)
                    ?? throw new InvalidDataException("播放源映射文件无效。");
                _mappings = envelope.Mappings;
                _idMigrationVersion = envelope.IdMigrationVersion;
                _lastRemapSeq = envelope.LastRemapSeq;
            }
            else _mappings = doc.RootElement.Deserialize<Dictionary<string, string>>(SerializerOptions) ?? [];
        }
    }

    private async Task<int?> ApplyPublicIdsAsync(
        int? lookupAnimeId, CancellationToken cancellationToken, PlayerPublicIdChanges? changes = null)
    {
        await EnsureLocalLoadedAsync(cancellationToken);
        changes ??= await PlayerPublicIdChanges.ReadAsync(_migrationPath, _remapPath, cancellationToken);
        if (_idMigrationVersion < 9 && await changes.ReadMigrationAsync() is { } ids)
        {
            var rewritten = PlayerPublicIds.Rewrite(_mappings!, ids, sourceKeys: true);
            await SaveValuesAsync(rewritten, 9, cancellationToken);
            _mappings = rewritten;
            _idMigrationVersion = 9;
        }
        var remaps = changes.Remaps.Where(row => row.Seq > _lastRemapSeq).ToList();
        if (remaps.Count > 0)
        {
            var routes = CaptureRoutes();
            var rewritten = PlayerPublicIds.ApplyRemaps(_mappings!, remaps, sourceKeys: true,
                beforeRemap: (remap, values) => FollowRemap(routes, remap, values));
            var seq = remaps[^1].Seq;
            await SaveValuesAsync(rewritten, _idMigrationVersion, cancellationToken, seq);
            _mappings = rewritten;
            _lastRemapSeq = seq;
            PublishRoutes(routes);
        }
        return lookupAnimeId is { } animeId ? changes.ResolveCurrentId(animeId) : null;
    }

    private List<LookupRoute> CaptureRoutes()
    {
        RemoveExpiredLookups();
        var routes = new List<LookupRoute>(_lookups.Count);
        foreach (var reference in _lookups)
        {
            if (reference.TryGetTarget(out var lookup))
                routes.Add(new LookupRoute(lookup));
        }
        return routes;
    }

    private void RemoveExpiredLookups()
        => _lookups.RemoveAll(reference => !reference.TryGetTarget(out _));

    private static void FollowRemap(
        IEnumerable<LookupRoute> routes,
        PlayerPublicIds.RemapEntry remap,
        IReadOnlyDictionary<string, string> values)
    {
        // 查找仍读原编号快照；只在已读改绑的提交计划中跟进写回目标。
        foreach (var route in routes.Where(route => route.AnimeId == remap.From))
        {
            var targetPrefix = GetKey(remap.To, "");
            foreach (var key in values.Keys)
            {
                if (key.StartsWith(targetPrefix, StringComparison.Ordinal))
                    route.ShadowedSources.Add(key[targetPrefix.Length..]);
            }
            route.AnimeId = remap.To;
        }
    }

    private static void PublishRoutes(IEnumerable<LookupRoute> routes)
    {
        foreach (var route in routes)
        {
            route.Lookup.CurrentAnimeId = route.AnimeId;
            route.Lookup.ShadowedSources = route.ShadowedSources;
        }
    }

    private static Dictionary<string, string> ReadAnimeMappings(Dictionary<string, string> mappings, int animeId)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var prefix = GetKey(animeId, "");
        foreach (var pair in mappings)
        {
            if (pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                values[pair.Key[prefix.Length..]] = pair.Value;
        }
        return values;
    }

    private sealed class LookupRoute(SourceMappingLookup lookup)
    {
        public SourceMappingLookup Lookup { get; } = lookup;
        public int AnimeId { get; set; } = lookup.CurrentAnimeId;
        public HashSet<string> ShadowedSources { get; } = new(lookup.ShadowedSources, StringComparer.Ordinal);
    }

    private async Task SaveValuesAsync(Dictionary<string, string> mappings, int version, CancellationToken cancellationToken, long? seq = null)
    {
        if (_path is null)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var lastSeq = seq ?? _lastRemapSeq;
            var value = version >= 9 || lastSeq > 0 ? (object)new MappingFile(version, mappings, lastSeq) : mappings;
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(value, SerializerOptions), cancellationToken);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    private static string GetKey(int animeId, string sourceId)
        => $"{animeId}:{sourceId}";
    private sealed record MappingFile(int IdMigrationVersion, Dictionary<string, string> Mappings, long LastRemapSeq = 0);
}
