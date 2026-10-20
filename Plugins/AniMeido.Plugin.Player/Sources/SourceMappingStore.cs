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

    internal async Task EnsurePublicIdsAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await EnsureLoadedAsync(ct); }
        finally { _gate.Release(); }
    }

    public async Task<string?> GetAsync(
        int animeId,
        string sourceId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            return _mappings!.GetValueOrDefault(GetKey(animeId, sourceId));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(
        int animeId,
        string sourceId,
        string remoteId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            _mappings![GetKey(animeId, sourceId)] = remoteId;
            await SaveAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(
        int animeId,
        string sourceId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            if (_mappings!.Remove(GetKey(animeId, sourceId)))
            {
                await SaveAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
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
        if (_idMigrationVersion < 9 && await PlayerPublicIds.ReadAsync(_migrationPath, cancellationToken) is { } ids)
        {
            var rewritten = PlayerPublicIds.Rewrite(_mappings, ids, sourceKeys: true);
            await SaveValuesAsync(rewritten, 9, cancellationToken);
            _mappings = rewritten;
            _idMigrationVersion = 9;
        }
        var remaps = await PlayerPublicIds.ReadRemapsAsync(_remapPath, cancellationToken);
        if (remaps.Any(r => r.Seq > _lastRemapSeq))
        {
            var rewritten = PlayerPublicIds.ApplyRemaps(_mappings, remaps.Where(r => r.Seq > _lastRemapSeq), sourceKeys: true);
            var seq = remaps[^1].Seq;
            await SaveValuesAsync(rewritten, _idMigrationVersion, cancellationToken, seq);
            _mappings = rewritten; _lastRemapSeq = seq;
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
        => await SaveValuesAsync(_mappings!, _idMigrationVersion, cancellationToken);

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
