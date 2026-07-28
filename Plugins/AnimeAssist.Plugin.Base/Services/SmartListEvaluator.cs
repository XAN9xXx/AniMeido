using AniMeido.Plugin.Base.Models;
using System.Globalization;

namespace AniMeido.Plugin.Base.Services;

public static class SmartListEvaluator
{
    public const int SchemaVersion = 1;
    public const int MaxNestingDepth = 2;

    public static bool Matches(
        SmartListRuleGroup group,
        SmartListCandidate candidate)
        => Matches(group, candidate, 1);

    public static IReadOnlyList<SmartListCandidate> Apply(
        SmartListDefinition definition,
        IEnumerable<SmartListCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(candidates);
        if (definition.SchemaVersion != SchemaVersion)
        {
            throw new InvalidDataException(
                $"不支持的智能列表版本：{definition.SchemaVersion}。");
        }

        var matches = candidates.Where(item => Matches(definition.Rules, item));
        return ApplySort(matches, definition.Sort).ToList();
    }

    private static bool Matches(
        SmartListRuleGroup group,
        SmartListCandidate candidate,
        int depth)
    {
        if (depth > MaxNestingDepth)
        {
            throw new InvalidDataException("智能列表条件组最多嵌套两层。");
        }

        var results = group.Conditions.Select(condition =>
            Matches(condition, candidate)).ToList();
        if (group.Groups is not null)
        {
            results.AddRange(group.Groups.Select(child =>
                Matches(child, candidate, depth + 1)));
        }

        return group.Mode == SmartListGroupMode.All
            ? results.All(static result => result)
            : results.Any(static result => result);
    }

    private static bool Matches(
        SmartListCondition condition,
        SmartListCandidate candidate)
    {
        var value = GetValue(condition.Field, candidate);
        if (condition.Operator == SmartListOperator.IsEmpty)
        {
            return IsEmpty(value);
        }
        if (condition.Operator == SmartListOperator.IsNotEmpty)
        {
            return !IsEmpty(value);
        }

        var expected = condition.Value?.Trim() ?? string.Empty;
        return value switch
        {
            IReadOnlyList<string> values => MatchList(
                values,
                condition.Operator,
                expected),
            DateOnly date => MatchDate(date, condition.Operator, expected),
            DateTimeOffset time => MatchDateTime(
                time,
                condition.Operator,
                expected),
            bool flag => MatchBoolean(flag, condition.Operator, expected),
            int number => MatchNumber(number, condition.Operator, expected),
            string text => MatchText(text, condition.Operator, expected),
            null => false,
            _ => false,
        };
    }

    private static object? GetValue(
        SmartListField field,
        SmartListCandidate item)
        => field switch
        {
            SmartListField.TrackingStatus => item.TrackingStatus,
            SmartListField.CurrentEpisode => item.CurrentEpisode,
            SmartListField.HasIncompleteEpisode =>
                item.HasIncompleteEpisode,
            SmartListField.PlanPriority => item.PlanPriority,
            SmartListField.TargetDate => item.TargetDate,
            SmartListField.IsOverdue => item.IsOverdue,
            SmartListField.Weekday => item.Weekday,
            SmartListField.AirDate => item.AirDate,
            SmartListField.Title => item.Title,
            SmartListField.Tags => item.Tags,
            SmartListField.LastWatchedAt => item.LastWatchedAt,
            SmartListField.UpdatedAt => item.UpdatedAt,
            _ => null,
        };

    private static bool MatchText(
        string actual,
        SmartListOperator op,
        string expected)
        => op switch
        {
            SmartListOperator.Equals => string.Equals(
                actual,
                expected,
                StringComparison.OrdinalIgnoreCase),
            SmartListOperator.NotEquals => !string.Equals(
                actual,
                expected,
                StringComparison.OrdinalIgnoreCase),
            SmartListOperator.Contains => actual.Contains(
                expected,
                StringComparison.OrdinalIgnoreCase),
            SmartListOperator.NotContains => !actual.Contains(
                expected,
                StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    private static bool MatchList(
        IReadOnlyList<string> values,
        SmartListOperator op,
        string expected)
    {
        var expectedValues = expected.Split(
            ',',
            StringSplitOptions.TrimEntries
                | StringSplitOptions.RemoveEmptyEntries);
        return op switch
        {
            SmartListOperator.Contains
                or SmartListOperator.ContainsAny =>
                expectedValues.Any(expectedValue => values.Contains(
                    expectedValue,
                    StringComparer.OrdinalIgnoreCase)),
            SmartListOperator.NotContains =>
                expectedValues.All(expectedValue => !values.Contains(
                    expectedValue,
                    StringComparer.OrdinalIgnoreCase)),
            SmartListOperator.ContainsAll =>
                expectedValues.All(expectedValue => values.Contains(
                    expectedValue,
                    StringComparer.OrdinalIgnoreCase)),
            _ => false,
        };
    }

    private static bool MatchNumber(
        int actual,
        SmartListOperator op,
        string expected)
        => int.TryParse(
            expected,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var number)
            && Compare(actual, number, op);

    private static bool MatchBoolean(
        bool actual,
        SmartListOperator op,
        string expected)
        => bool.TryParse(expected, out var flag)
            && op switch
            {
                SmartListOperator.Equals => actual == flag,
                SmartListOperator.NotEquals => actual != flag,
                _ => false,
            };

    private static bool MatchDate(
        DateOnly actual,
        SmartListOperator op,
        string expected)
        => DateOnly.TryParse(
            expected,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date)
            && Compare(actual.DayNumber, date.DayNumber, op);

    private static bool MatchDateTime(
        DateTimeOffset actual,
        SmartListOperator op,
        string expected)
        => DateTimeOffset.TryParse(
            expected,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var time)
            && Compare(
                actual.UtcTicks,
                time.UtcTicks,
                op);

    private static bool Compare<T>(
        T actual,
        T expected,
        SmartListOperator op)
        where T : IComparable<T>
    {
        var comparison = actual.CompareTo(expected);
        return op switch
        {
            SmartListOperator.Equals => comparison == 0,
            SmartListOperator.NotEquals => comparison != 0,
            SmartListOperator.GreaterThan
                or SmartListOperator.After => comparison > 0,
            SmartListOperator.GreaterThanOrEqual => comparison >= 0,
            SmartListOperator.LessThan
                or SmartListOperator.Before => comparison < 0,
            SmartListOperator.LessThanOrEqual => comparison <= 0,
            _ => false,
        };
    }

    private static bool IsEmpty(object? value)
        => value is null
            || value is string text && string.IsNullOrWhiteSpace(text)
            || value is IReadOnlyCollection<string> values
                && values.Count == 0;

    private static IEnumerable<SmartListCandidate> ApplySort(
        IEnumerable<SmartListCandidate> candidates,
        SmartListSort? sort)
    {
        if (sort is null)
        {
            return candidates;
        }

        Func<SmartListCandidate, IComparable?> key = sort.Field switch
        {
            SmartListField.Title => item => item.Title,
            SmartListField.TrackingStatus => item => item.TrackingStatus,
            SmartListField.CurrentEpisode => item => item.CurrentEpisode,
            SmartListField.PlanPriority => item => item.PlanPriority,
            SmartListField.TargetDate => item => item.TargetDate,
            SmartListField.Weekday => item => item.Weekday,
            SmartListField.AirDate => item => item.AirDate,
            SmartListField.LastWatchedAt => item => item.LastWatchedAt,
            SmartListField.UpdatedAt => item => item.UpdatedAt,
            _ => item => item.Title,
        };
        return sort.Descending
            ? candidates.OrderByDescending(key)
            : candidates.OrderBy(key);
    }
}
