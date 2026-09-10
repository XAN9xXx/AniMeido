using System.Text.Json;
using AniMeido.Plugin.Player.Sources.Packages;

namespace AniMeido.Plugin.Player.Sources.Web;

internal sealed class PlayerRuntimeSettingsStore
{
    internal const int DefaultTimeoutSeconds = 30;
    internal const int MinimumTimeoutSeconds = 10;
    internal const int MaximumTimeoutSeconds = 120;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string? _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PlayerRuntimeSettingsStore()
        : this(Path.Combine(
            SourcePackageInstaller.GetSourcesDirectory(),
            "Settings",
            "runtime.json"))
    {
    }

    internal PlayerRuntimeSettingsStore(string? path)
    {
        _path = path;
    }

    public async Task<PlayerRuntimeSettings> ReadAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteAsync(
        PlayerRuntimeSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings);
        await _gate.WaitAsync(cancellationToken);
        try
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
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TimeSpan> GetEffectiveTimeoutAsync(
        string sourceId,
        TimeSpan? sourceDeclaredTimeout,
        CancellationToken cancellationToken)
    {
        var settings = await ReadAsync(cancellationToken);
        if (settings.SourceTimeoutSeconds.TryGetValue(
                sourceId,
                out var overrideSeconds))
        {
            return TimeSpan.FromSeconds(overrideSeconds);
        }

        if (sourceDeclaredTimeout is { } declared)
        {
            return TimeSpan.FromSeconds(Math.Clamp(
                declared.TotalSeconds,
                MinimumTimeoutSeconds,
                MaximumTimeoutSeconds));
        }

        return TimeSpan.FromSeconds(settings.GlobalTimeoutSeconds);
    }

    private async Task<PlayerRuntimeSettings> ReadCoreAsync(
        CancellationToken cancellationToken)
    {
        if (_path is null || !File.Exists(_path))
        {
            return PlayerRuntimeSettings.CreateDefault();
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            var settings =
                await JsonSerializer.DeserializeAsync<PlayerRuntimeSettings>(
                    stream,
                    SerializerOptions,
                    cancellationToken);
            if (settings is null)
            {
                return PlayerRuntimeSettings.CreateDefault();
            }

            Validate(settings);
            return settings;
        }
        catch (Exception ex)
            when (ex is JsonException
                or IOException
                or UnauthorizedAccessException
                or ArgumentOutOfRangeException)
        {
            return PlayerRuntimeSettings.CreateDefault();
        }
    }

    private static void Validate(PlayerRuntimeSettings settings)
    {
        if (settings.SchemaVersion != 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                "不支持的播放器运行时设置版本。");
        }

        ValidateTimeout(settings.GlobalTimeoutSeconds);
        foreach (var timeout in settings.SourceTimeoutSeconds.Values)
        {
            ValidateTimeout(timeout);
        }
    }

    private static void ValidateTimeout(int seconds)
    {
        if (seconds is < MinimumTimeoutSeconds or > MaximumTimeoutSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(seconds),
                $"解析超时必须为 {MinimumTimeoutSeconds}–{MaximumTimeoutSeconds} 秒。");
        }
    }
}

internal sealed class PlayerRuntimeSettings
{
    public int SchemaVersion { get; set; } = 1;

    public int GlobalTimeoutSeconds { get; set; } =
        PlayerRuntimeSettingsStore.DefaultTimeoutSeconds;

    public Dictionary<string, int> SourceTimeoutSeconds { get; set; } =
        new(StringComparer.Ordinal);

    public static PlayerRuntimeSettings CreateDefault() => new();
}
