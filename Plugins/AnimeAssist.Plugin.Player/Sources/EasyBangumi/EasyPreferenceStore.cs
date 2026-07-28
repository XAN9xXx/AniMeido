using System.Text.Json;
using AniMeido.Plugin.Player.Sources.Packages;

namespace AniMeido.Plugin.Player.Sources.EasyBangumi;

internal sealed class EasyPreferenceStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EasyPreferenceStore()
        : this(Path.Combine(
            SourcePackageInstaller.GetSourcesDirectory(),
            "Settings"))
    {
    }

    internal EasyPreferenceStore(string directory)
    {
        _directory = directory;
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = GetPath(sourceId);
            if (!File.Exists(path))
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(
                stream,
                SerializerOptions,
                cancellationToken)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteAsync(
        string sourceId,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_directory);
            var path = GetPath(sourceId);
            var temporaryPath = $"{path}.tmp";
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(values, SerializerOptions),
                cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetPath(string sourceId)
        => Path.Combine(_directory, $"{sourceId}.json");
}
