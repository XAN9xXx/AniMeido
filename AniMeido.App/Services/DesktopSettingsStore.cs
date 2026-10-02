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
            return await LoadCoreAsync(cancellationToken);
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
            await SaveCoreAsync(settings, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>在同一锁内读取、修改并保存，避免更新偏好与托盘设置互相覆盖。</summary>
    internal async Task<DesktopSettings> UpdateAsync(
        Func<DesktopSettings, DesktopSettings> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var settings = update(await LoadCoreAsync(cancellationToken));
            await SaveCoreAsync(settings, cancellationToken);
            return settings;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<DesktopSettings> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            return new DesktopSettings();

        try
        {
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
    }

    private async Task SaveCoreAsync(DesktopSettings settings, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
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
}

public sealed record DesktopSettings
{
    public bool KeepInTrayOnClose { get; init; }
    public string? SkippedUpdateVersion { get; init; }
}
