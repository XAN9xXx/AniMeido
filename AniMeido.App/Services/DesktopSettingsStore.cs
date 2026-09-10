using AniMeido.Contracts;
using System.Text.Json;

namespace AniMeido.App.Services;

public sealed class DesktopSettingsStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DesktopSettingsStore(IAppDataPaths paths)
    {
        _path = Path.Combine(
            Path.GetDirectoryName(paths.DatabasePath)
                ?? throw new InvalidOperationException(
                    "应用数据目录不可用。"),
            "desktop-settings.json");
    }

    public async Task<DesktopSettings> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path))
            {
                return new DesktopSettings();
            }

            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<DesktopSettings>(
                stream,
                cancellationToken: cancellationToken)
                ?? new DesktopSettings();
        }
        catch (JsonException)
        {
            return new DesktopSettings();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        DesktopSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(_path)!);
            var temporaryPath = _path + ".tmp";
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    settings,
                    cancellationToken: cancellationToken);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed record DesktopSettings
{
    public bool KeepInTrayOnClose { get; init; }
}
