using AniMeido.Plugin.Player.Sources.Packages;
using System.Text.Json;
using System.Globalization;

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
    private readonly string? _remapPath;
    private readonly List<WeakReference<PlayerRouteIdentity>> _routeIdentities = [];

    public PlayerExperienceSettingsStore()
        : this(Path.Combine(
            SourcePackageInstaller.GetSourcesDirectory(),
            "Settings",
            "experience.json"), PlayerPublicIds.SharedMigrationPath, PlayerPublicIds.SharedRemapPath)
    {
    }

    internal PlayerExperienceSettingsStore(string? path, string? migrationPath = null, string? remapPath = null)
    {
        _path = path;
        _migrationPath = migrationPath;
        _remapPath = remapPath;
    }

    public Task<PlayerExperienceReadResult> ReadAsync(
        CancellationToken cancellationToken)
        => WithGateAsync(ct => ReadCoreAsync(ct, allowReadFallback: true), cancellationToken);

    internal Task<PlayerExperienceReadResult> ReadAndApplyPublicIdsAsync(
        CancellationToken cancellationToken)
        => WithGateAsync(ct => ReadAndMigrateAsync(ct, allowReadFallback: true), cancellationToken);

    internal Task<PlayerPublicIdChanges> ReadPublicIdChangesAsync(CancellationToken cancellationToken)
        => Task.Run(() => PlayerPublicIdChanges.ReadAsync(_migrationPath, _remapPath, cancellationToken), cancellationToken);

    internal Task<PlayerExperienceReadResult> ReadAndApplyPublicIdsAsync(
        PlayerPublicIdChanges changes, CancellationToken cancellationToken)
        => WithGateAsync(ct => ReadAndMigrateAsync(ct, allowReadFallback: true, changes), cancellationToken);

    internal Task<PlayerRouteIdentity> BeginRouteAsync(long animeId, CancellationToken cancellationToken)
        => WithGateAsync(ct => Task.FromResult(RegisterRouteIdentity(animeId)), cancellationToken);

    internal Task<PlayerRoutePreparation> PrepareRouteAsync(long animeId, CancellationToken cancellationToken)
        => WithGateAsync(async ct =>
        {
            var changes = await PlayerPublicIdChanges.ReadAsync(_migrationPath, _remapPath, ct);
            var result = await ReadAndMigrateAsync(ct, allowReadFallback: true, changes);
            var identity = RegisterRouteIdentity(animeId);
            var sequence = changes.Remaps.Count == 0 ? 0 : changes.Remaps[^1].Seq;
            identity.Publish(changes.ResolveCurrentId(checked((int)animeId)), isShadowed: false, appliedRemapSeq: sequence);
            return new PlayerRoutePreparation(identity, result, changes);
        }, cancellationToken);

    internal Task<PlayerRoutePreparation> PrepareRouteAsync(
        PlayerRouteIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return WithGateAsync(ct => PrepareRouteCoreAsync(identity, ct), cancellationToken);
    }

    private async Task<PlayerRoutePreparation> PrepareRouteCoreAsync(
        PlayerRouteIdentity identity, CancellationToken cancellationToken)
    {
        var changes = await PlayerPublicIdChanges.ReadAsync(_migrationPath, _remapPath, cancellationToken);
        var result = await ReadAndMigrateAsync(cancellationToken, allowReadFallback: true, changes);
        return new PlayerRoutePreparation(identity, result, changes);
    }

    private PlayerRouteIdentity RegisterRouteIdentity(long animeId)
    {
        _routeIdentities.RemoveAll(reference => !reference.TryGetTarget(out _));
        var identity = new PlayerRouteIdentity(animeId);
        _routeIdentities.Add(new WeakReference<PlayerRouteIdentity>(identity));
        return identity;
    }

    public Task UpdatePreferencesAsync(
        double volume,
        bool muted,
        double speed,
        bool autoFallbackEnabled,
        int windowWidth,
        int windowHeight,
        CancellationToken cancellationToken)
        => WithGateAsync(async ct =>
        {
            var settings = (await ReadCoreAsync(ct, allowReadFallback: false)).Settings;
            settings.Volume = Math.Clamp(volume, 0, 100);
            settings.IsMuted = muted;
            settings.Speed = Math.Clamp(speed, 0.5, 2);
            settings.AutoFallbackEnabled = autoFallbackEnabled;
            settings.WindowWidth = Math.Clamp(windowWidth, 800, 3840);
            settings.WindowHeight = Math.Clamp(windowHeight, 520, 2160);
            await WriteCoreAsync(settings, ct);
            return true;
        }, cancellationToken);

    public Task<PlayerExperienceSettings> RecordRouteResultAsync(
        long animeId,
        string routeKey,
        bool succeeded,
        TimeSpan latency,
        CancellationToken cancellationToken)
        => RecordRouteResultCoreAsync(animeId, null, routeKey, succeeded, latency, cancellationToken);

    internal Task<PlayerExperienceSettings> RecordRouteResultAsync(
        PlayerRouteIdentity identity,
        string routeKey,
        bool succeeded,
        TimeSpan latency,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return RecordRouteResultCoreAsync(
            identity.OriginalAnimeId, identity, routeKey, succeeded, latency, cancellationToken);
    }

    private Task<PlayerExperienceSettings> RecordRouteResultCoreAsync(
        long animeId,
        PlayerRouteIdentity? identity,
        string routeKey,
        bool succeeded,
        TimeSpan latency,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeKey);
        return WithGateAsync(async ct =>
        {
            var settings = (await ReadCoreAsync(ct, allowReadFallback: false)).Settings;
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
            if (succeeded && identity?.IsShadowed != true)
            {
                var currentAnimeId = identity?.CurrentAnimeId ?? animeId;
                settings.PreferredRouteByAnime[currentAnimeId.ToString(CultureInfo.InvariantCulture)] = routeKey;
            }

            await WriteCoreAsync(settings, ct);
            return settings;
        }, cancellationToken);
    }

    private async Task<T> WithGateAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        // 先按调用顺序排队，再后台执行整次读改写，避免旧偏好晚于新偏好落盘。
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

    private async Task<PlayerExperienceReadResult> ReadCoreAsync(
        CancellationToken cancellationToken,
        bool allowReadFallback)
    {
        if (_path is null || !File.Exists(_path))
        {
            return new PlayerExperienceReadResult(new PlayerExperienceSettings(), null);
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            var settings =
                await JsonSerializer.DeserializeAsync<PlayerExperienceSettings>(
                    stream,
                    SerializerOptions,
                    cancellationToken);
            if (settings is { SchemaVersion: 1 })
                return new PlayerExperienceReadResult(Normalize(settings), null);

            var failure = new InvalidDataException("Player 设置无效或版本不兼容，未保存文件。");
            if (allowReadFallback)
                return new PlayerExperienceReadResult(new PlayerExperienceSettings(), failure);

            throw failure;
        }
        catch (Exception ex)
            when (allowReadFallback && ex is (JsonException
                or IOException
                or UnauthorizedAccessException))
        {
            return new PlayerExperienceReadResult(new PlayerExperienceSettings(), ex);
        }
    }

    private async Task<PlayerExperienceReadResult> ReadAndMigrateAsync(
        CancellationToken ct, bool allowReadFallback, PlayerPublicIdChanges? changes = null)
    {
        var result = await ReadCoreAsync(ct, allowReadFallback);
        if (result.FallbackReason is not null)
            return result;

        var settings = result.Settings;
        changes ??= await PlayerPublicIdChanges.ReadAsync(_migrationPath, _remapPath, ct);
        var ids = settings.IdMigrationVersion < 9 ? await changes.ReadMigrationAsync() : null;
        var remaps = changes.Remaps.Where(row => row.Seq > settings.LastRemapSeq).ToList();
        if (ids is null && remaps.Count == 0)
            return result;

        var identities = CaptureRouteIdentities();
        if (settings.IdMigrationVersion < 9 && ids is not null)
        {
            settings.PreferredRouteByAnime = PlayerPublicIds.Rewrite(settings.PreferredRouteByAnime, ids, sourceKeys: false);
            settings.IdMigrationVersion = 9;
            // C2a 只转换文件中的旧 Bangumi 键；活跃身份已经是公共 ID。
        }
        settings.PreferredRouteByAnime = PlayerPublicIds.ApplyRemaps(
            settings.PreferredRouteByAnime,
            remaps,
            sourceKeys: false,
            beforeRemap: (entry, preferences) =>
            {
                var targetExists = preferences.ContainsKey(entry.To.ToString(CultureInfo.InvariantCulture));
                for (var index = 0; index < identities.Count; index++)
                {
                    var identity = identities[index];
                    if (entry.Seq <= identity.AppliedRemapSeq)
                        continue;

                    identities[index] = identity with
                    {
                        AnimeId = identity.AnimeId == entry.From ? entry.To : identity.AnimeId,
                        IsShadowed = identity.IsShadowed || (identity.AnimeId == entry.From && targetExists),
                        AppliedRemapSeq = entry.Seq,
                    };
                }
            });
        if (remaps.Count > 0)
            settings.LastRemapSeq = Math.Max(settings.LastRemapSeq, remaps[^1].Seq);
        await WriteCoreAsync(settings, ct);
        // 保存成功后才改变仍在播放的身份，失败时旧请求继续使用旧编号。
        foreach (var identity in identities)
            identity.Identity.Publish(identity.AnimeId, identity.IsShadowed, identity.AppliedRemapSeq);
        return new PlayerExperienceReadResult(settings, null);
    }

    private List<RouteIdentityUpdate> CaptureRouteIdentities()
    {
        var identities = new List<RouteIdentityUpdate>(_routeIdentities.Count);
        for (var index = _routeIdentities.Count - 1; index >= 0; index--)
        {
            if (_routeIdentities[index].TryGetTarget(out var identity))
                identities.Add(new RouteIdentityUpdate(
                    identity, identity.CurrentAnimeId, identity.IsShadowed, identity.AppliedRemapSeq));
            else
                _routeIdentities.RemoveAt(index);
        }
        return identities;
    }

    private sealed record RouteIdentityUpdate(
        PlayerRouteIdentity Identity, long AnimeId, bool IsShadowed, long AppliedRemapSeq);

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

internal sealed record PlayerExperienceReadResult(
    PlayerExperienceSettings Settings,
    Exception? FallbackReason);

internal sealed class PlayerExperienceSettings
{
    public int SchemaVersion { get; set; } = 1;
    public int IdMigrationVersion { get; set; }
    public long LastRemapSeq { get; set; }

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
