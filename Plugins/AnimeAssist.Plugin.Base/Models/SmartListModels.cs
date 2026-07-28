using System.Text.Json.Serialization;

namespace AniMeido.Plugin.Base.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SmartListGroupMode
{
    All,
    Any,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SmartListField
{
    TrackingStatus,
    CurrentEpisode,
    HasIncompleteEpisode,
    PlanPriority,
    TargetDate,
    IsOverdue,
    Weekday,
    AirDate,
    Title,
    Tags,
    LastWatchedAt,
    UpdatedAt,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SmartListOperator
{
    Equals,
    NotEquals,
    Contains,
    NotContains,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Before,
    After,
    IsEmpty,
    IsNotEmpty,
    ContainsAny,
    ContainsAll,
}

public sealed record SmartListCondition(
    SmartListField Field,
    SmartListOperator Operator,
    string? Value);

public sealed record SmartListRuleGroup(
    SmartListGroupMode Mode,
    IReadOnlyList<SmartListCondition> Conditions,
    IReadOnlyList<SmartListRuleGroup>? Groups = null);

public sealed record SmartListSort(
    SmartListField Field,
    bool Descending);

public sealed record SmartListDefinition(
    string Id,
    string Name,
    int SchemaVersion,
    SmartListRuleGroup Rules,
    SmartListSort? Sort,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SmartListCandidate(
    int AnimeId,
    string Title,
    int? TrackingStatus,
    int CurrentEpisode,
    bool HasIncompleteEpisode,
    int? PlanPriority,
    DateOnly? TargetDate,
    bool IsOverdue,
    int? Weekday,
    DateOnly? AirDate,
    IReadOnlyList<string> Tags,
    DateTimeOffset? LastWatchedAt,
    DateTimeOffset? UpdatedAt);
