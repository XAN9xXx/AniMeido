using System.Text.Json.Serialization;

namespace AniMeido.Plugin.Base.Models;

// 只用于旧档案中规则 JSON 的标签迁移；不提供智能列表编辑或执行入口。

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
