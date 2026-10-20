using AniMeido.Plugin.Player.Sources.Packages;
using System.Text.Json;

namespace AniMeido.Plugin.Player.Playback;

internal sealed class PlayerExperienceSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string? _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string? _migrationPath;

    public PlayerExperienceSettingsStore()
        : this(Path.Combine(
            SourcePackageInstaller.GetSourcesDirectory(),
            "Settings",
            "experience.json"), PlayerPublicIds.SharedMigrationPath)
    {
    }

    internal PlayerExperienceSettingsStore(string? path, string? migrationPath = null)
    {
        _path = path;
        _migrationPath = migrationPath;
    }

    public Task<PlayerExperienceSettings> ReadAsync(
        CancellationToken cancellationToken)
        => WithGateAsync(ReadAndMigrateAsync, cancellationToken);

    public async Task UpdatePreferencesAsync(
        double volume,
        bool muted,
        double speed,
        bool autoFallbackEnabled,
        int windowWidth,
        int windowHeight,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var settings = await ReadAndMigrateAsync(cancellationToken);
            settings.Volume = Math.Clamp(volume, 0, 100);
            settings.IsMuted = muted;
            settings.Speed = Math.Clamp(speed, 0.5, 2);
            settings.AutoFallbackEnabled = autoFallbackEnabled;
            settings.WindowWidth = Math.Clamp(windowWidth, 800, 3840);
            settings.WindowHeight = Math.Clamp(windowHeight, 520, 2160);
            await WriteCoreAsync(settings, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordRouteResultAsync(
        long animeId,
        string routeKey,
        bool succeeded,
        TimeSpan latency,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeKey);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var settings = await ReadAndMigrateAsync(cancellationToken);
            settings.RouteHealth.TryGetValue(routeKey, out var previous);
            previous ??= new RouteHealthRecord();
            settings.RouteHealth[routeKey] = succeeded
                ? previous with
                {
                    SuccessCount = previous.SuccessCount + 1,
                    ConsecutiveFailures = 0,
                    LastLatencyMilliseconds = Math.Max(0, latency.TotalMilliseconds),
                    LastSuccessUtc = DateTimeOffset.UtcNow,
                }
                : previous with
                {
                    FailureCount = previous.FailureCount + 1,
                    ConsecutiveFailures = previous.ConsecutiveFailures + 1,
                    LastLatencyMilliseconds = Math.Max(0, latency.TotalMilliseconds),
                    LastFailureUtc = DateTimeOffset.UtcNow,
                };
            if (succeeded)
            {
                settings.PreferredRouteByAnime[animeId.ToString()] = routeKey;
            }

            await WriteCoreAsync(settings, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> WithGateAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await action(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PlayerExperienceSettings> ReadCoreAsync(
        CancellationToken cancellationToken)
    {
        if (_path is null || !File.Exists(_path))
        {
            return new PlayerExperienceSettings();
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            var settings =
                await JsonSerializer.DeserializeAsync<PlayerExperienceSettings>(
                    stream,
                    SerializerOptions,
                    cancellationToken);
            return settings is { SchemaVersion: 1 }
                ? Normalize(settings)
                : new PlayerExperienceSettings();
        }
        catch (Exception ex)
            when (ex is JsonException
                or IOException
                or UnauthorizedAccessException)
        {
            return new PlayerExperienceSettings();
        }
    }

    private async Task<PlayerExperienceSettings> ReadAndMigrateAsync(CancellationToken ct)
    {
        var settings = await ReadCoreAsync(ct);
        if (settings.IdMigrationVersion >= 9 || await PlayerPublicIds.ReadAsync(_migrationPath, ct) is not { } ids) return settings;
        if (_path is not null && File.Exists(_path))
        {
            settings = JsonSerializer.Deserialize<PlayerExperienceSettings>(await File.ReadAllTextAsync(_path, ct), SerializerOptions)
                ?? throw new InvalidDataException("Player 设置无效，未迁移或覆盖文件。");
            if (settings.SchemaVersion != 1) throw new InvalidDataException("Player 设置版本不兼容。");
            settings = Normalize(settings);
        }
        settings.PreferredRouteByAnime = PlayerPublicIds.Rewrite(settings.PreferredRouteByAnime, ids, sourceKeys: false);
        settings.IdMigrationVersion = 9;
        await WriteCoreAsync(settings, ct);
        return settings;
    }

    private async Task WriteCoreAsync(
        PlayerExperienceSettings settings,
        CancellationToken cancellationToken)
    {
        if (_path is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(settings, SerializerOptions),
                cancellationToken);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static PlayerExperienceSettings Normalize(
        PlayerExperienceSettings settings)
    {
        settings.Volume = Math.Clamp(settings.Volume, 0, 100);
        settings.Speed = Math.Clamp(settings.Speed, 0.5, 2);
        settings.WindowWidth = Math.Clamp(settings.WindowWidth, 800, 3840);
        settings.WindowHeight = Math.Clamp(settings.WindowHeight, 520, 2160);
        settings.RouteHealth ??= new(StringComparer.Ordinal);
        settings.PreferredRouteByAnime ??= new(StringComparer.Ordinal);
        return settings;
    }
}

internal sealed class PlayerExperienceSettings
{
    public int SchemaVersion { get; set; } = 1;
    public int IdMigrationVersion { get; set; }

    public double Volume { get; set; } = 100;

    public bool IsMuted { get; set; }

    public double Speed { get; set; } = 1;

    public bool AutoFallbackEnabled { get; set; }

    public int WindowWidth { get; set; } = 1200;

    public int WindowHeight { get; set; } = 780;

    public Dictionary<string, RouteHealthRecord> RouteHealth { get; set; } =
        new(StringComparer.Ordinal);

    public Dictionary<string, string> PreferredRouteByAnime { get; set; } =
        new(StringComparer.Ordinal);
}

internal sealed record RouteHealthRecord
{
    public int SuccessCount { get; init; }

    public int FailureCount { get; init; }

    public int ConsecutiveFailures { get; init; }

    public double LastLatencyMilliseconds { get; init; }

    public DateTimeOffset? LastSuccessUtc { get; init; }

    public DateTimeOffset? LastFailureUtc { get; init; }
}
