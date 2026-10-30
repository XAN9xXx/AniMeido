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

    public Task<IReadOnlyDictionary<string, string>> ReadAsync(
        string sourceId,
        CancellationToken cancellationToken)
        => WithGateAsync<IReadOnlyDictionary<string, string>>(async ct =>
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
                ct)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }, cancellationToken);

    public Task WriteAsync(
        string sourceId,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
        => WithGateAsync(async ct =>
        {
            Directory.CreateDirectory(_directory);
            var path = GetPath(sourceId);
            var temporaryPath = $"{path}.tmp";
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(values, SerializerOptions),
                ct);
            File.Move(temporaryPath, path, overwrite: true);
            return true;
        }, cancellationToken);

    private async Task<T> WithGateAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        // 先按调用顺序排队，再后台执行，避免较早的保存晚于新选择落盘。
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => action(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetPath(string sourceId)
        => Path.Combine(_directory, $"{sourceId}.json");
}
