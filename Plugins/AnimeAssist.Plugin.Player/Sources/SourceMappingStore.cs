using System.Text.Json;

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

    public SourceMappingStore()
        : this(Path.Combine(
            Packages.SourcePackageInstaller.GetSourcesDirectory(),
            "Mappings",
            "mappings.json"))
    {
    }

    internal SourceMappingStore(string? path)
    {
        _path = path;
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
        if (_mappings is not null)
        {
            return;
        }

        if (_path is null || !File.Exists(_path))
        {
            _mappings = new Dictionary<string, string>(StringComparer.Ordinal);
            return;
        }

        await using var stream = File.OpenRead(_path);
        _mappings = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(
            stream,
            SerializerOptions,
            cancellationToken)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (_path is null)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = $"{_path}.tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(_mappings, SerializerOptions),
            cancellationToken);
        File.Move(temporaryPath, _path, overwrite: true);
    }

    private static string GetKey(int animeId, string sourceId)
        => $"{animeId}:{sourceId}";
}
