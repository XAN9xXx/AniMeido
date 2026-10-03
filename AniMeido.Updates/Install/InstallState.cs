using System.Text.Json.Serialization;

namespace AniMeido.Updates.Install;

public sealed record InstallState
{
    [JsonRequired]
    public int Schema { get; init; } = 1;
    [JsonRequired]
    public required string Current { get; init; }
    public string? Previous { get; init; }
    public string? Pending { get; init; }
    [JsonRequired]
    public bool CurrentHealthy { get; init; }
    [JsonRequired]
    public int FailedStarts { get; init; }
    public MigrationRecord? Migration { get; init; }
    public RolledBackNotice? RolledBackFrom { get; init; }
    [JsonRequired]
    public string[] FailedVersions { get; init; } = [];

    internal void Validate()
    {
        if (Schema != 1 || FailedStarts < 0 || !SemanticVersion.TryParse(Current, out _)
            || (Previous is not null && !SemanticVersion.TryParse(Previous, out _))
            || (Pending is not null && !SemanticVersion.TryParse(Pending, out _))
            || FailedVersions is null || FailedVersions.Any(v => !SemanticVersion.TryParse(v, out _))
            || (Migration is not null && (!SemanticVersion.TryParse(Migration.Version, out _)
                || string.IsNullOrWhiteSpace(Migration.Backup) || !Path.IsPathFullyQualified(Migration.Backup)))
            || (RolledBackFrom is not null && (!SemanticVersion.TryParse(RolledBackFrom.Version, out _)
                || RolledBackFrom.Reason is not (RolledBackNotice.StartFailed or RolledBackNotice.UserRestored))))
        {
            throw new InstallException("state.json 的安装状态无效或 schema 不受支持。");
        }
    }
}

public sealed record MigrationRecord
{
    [JsonRequired]
    public required string Version { get; init; }
    [JsonRequired]
    public required string Backup { get; init; }
}

public sealed record RolledBackNotice
{
    public const string StartFailed = "start_failed";
    public const string UserRestored = "user_restored";

    [JsonRequired]
    public required string Version { get; init; }
    [JsonRequired]
    public required string Reason { get; init; }
}

public sealed class InstallException(string message, Exception? innerException = null)
    : IOException(message, innerException);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false, WriteIndented = true)]
[JsonSerializable(typeof(InstallState))]
internal partial class InstallJsonContext : JsonSerializerContext;
