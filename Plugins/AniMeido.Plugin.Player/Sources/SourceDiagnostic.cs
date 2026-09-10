namespace AniMeido.Plugin.Player.Sources;

/// <summary>
/// Describes a recoverable source failure from the latest catalog operation.
/// </summary>
public sealed record SourceDiagnostic(
    string SourceId,
    string SourceName,
    string Operation,
    string Message);
