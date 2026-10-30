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

    public async Task<PlayerRuntimeReadResult> ReadAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () => ReadCoreAsync(cancellationToken, allowReadFallback: true),
                cancellationToken).ConfigureAwait(false);
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
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(
                () => WriteCoreAsync(settings, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WriteCoreAsync(
        PlayerRuntimeSettings settings,
        CancellationToken cancellationToken)
    {
        if (_path is null)
        {
            return;
        }

        await ReadCoreAsync(cancellationToken, allowReadFallback: false);
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

    public async Task<(TimeSpan Timeout, Exception? FallbackReason)> GetEffectiveTimeoutAsync(
        string sourceId,
        TimeSpan? sourceDeclaredTimeout,
        CancellationToken cancellationToken)
    {
        var result = await ReadAsync(cancellationToken);
        var settings = result.Settings;
        if (settings.SourceTimeoutSeconds.TryGetValue(
                sourceId,
                out var overrideSeconds))
        {
            return (TimeSpan.FromSeconds(overrideSeconds), result.FallbackReason);
        }

        if (sourceDeclaredTimeout is { } declared)
        {
            var seconds = Math.Clamp(
                declared.TotalSeconds,
                MinimumTimeoutSeconds,
                MaximumTimeoutSeconds);
            return (TimeSpan.FromSeconds(seconds), result.FallbackReason);
        }

        return (TimeSpan.FromSeconds(settings.GlobalTimeoutSeconds), result.FallbackReason);
    }

    private async Task<PlayerRuntimeReadResult> ReadCoreAsync(
        CancellationToken cancellationToken,
        bool allowReadFallback)
    {
        if (_path is null)
        {
            return new PlayerRuntimeReadResult(PlayerRuntimeSettings.CreateDefault(), null);
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
                throw new InvalidDataException("播放器运行时设置为空，未保存文件。");
            }

            Validate(settings);
            return new PlayerRuntimeReadResult(settings, null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new PlayerRuntimeReadResult(PlayerRuntimeSettings.CreateDefault(), null);
        }
        catch (Exception ex)
            when (allowReadFallback && ex is (JsonException
                or InvalidDataException
                or IOException
                or UnauthorizedAccessException
                or ArgumentOutOfRangeException))
        {
            return new PlayerRuntimeReadResult(PlayerRuntimeSettings.CreateDefault(), ex);
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

        if (settings.SourceTimeoutSeconds is null)
        {
            throw new InvalidDataException("播放器运行时设置缺少播放源超时记录，未保存文件。");
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

internal sealed record PlayerRuntimeReadResult(
    PlayerRuntimeSettings Settings,
    Exception? FallbackReason);

internal sealed class PlayerRuntimeSettings
{
    public int SchemaVersion { get; set; } = 1;

    public int GlobalTimeoutSeconds { get; set; } =
        PlayerRuntimeSettingsStore.DefaultTimeoutSeconds;

    public Dictionary<string, int> SourceTimeoutSeconds { get; set; } =
        new(StringComparer.Ordinal);

    public static PlayerRuntimeSettings CreateDefault() => new();
}
