using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace AniMeido.Updates;

/// <summary>SemVer 2.0.0；数字使用字符串比较，避免对规范未规定的位数施加整数上限。</summary>
public sealed class SemanticVersion : IComparable<SemanticVersion>, IComparable, IEquatable<SemanticVersion>
{
    private readonly string _original;
    private readonly string[] _core;
    private readonly string[] _prerelease;

    private SemanticVersion(string original, string[] core, string[] prerelease)
    {
        _original = original;
        _core = core;
        _prerelease = prerelease;
    }

    public bool IsPrerelease => _prerelease.Length != 0;

    public static bool TryParse(string? value, [NotNullWhen(true)] out SemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var buildStart = value.IndexOf('+');
        var precedence = buildStart < 0 ? value : value[..buildStart];
        if (buildStart >= 0 && !ValidIdentifiers(value[(buildStart + 1)..], false))
        {
            return false;
        }

        var prereleaseStart = precedence.IndexOf('-');
        var coreText = prereleaseStart < 0 ? precedence : precedence[..prereleaseStart];
        var core = coreText.Split('.');
        if (core.Length != 3 || core.Any(part => !IsNumeric(part) || HasLeadingZero(part)))
        {
            return false;
        }

        var prerelease = Array.Empty<string>();
        if (prereleaseStart >= 0)
        {
            var text = precedence[(prereleaseStart + 1)..];
            if (!ValidIdentifiers(text, true))
            {
                return false;
            }

            prerelease = text.Split('.');
        }

        version = new SemanticVersion(value, core, prerelease);
        return true;
    }

    public static SemanticVersion Parse(string value) =>
        TryParse(value, out var version)
            ? version
            : throw new UpdateException(UpdateErrorKind.InvalidVersion, "版本号不是合法的 SemVer 2.0.0。");

    public static SemanticVersion FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var text = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return Parse(text ?? throw new UpdateException(UpdateErrorKind.InvalidVersion, "程序集缺少完整版本号。"));
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        for (var i = 0; i < 3; i++)
        {
            var comparison = CompareNumeric(_core[i], other._core[i]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        if (!IsPrerelease || !other.IsPrerelease)
        {
            return IsPrerelease == other.IsPrerelease ? 0 : IsPrerelease ? -1 : 1;
        }

        for (var i = 0; i < Math.Min(_prerelease.Length, other._prerelease.Length); i++)
        {
            var left = _prerelease[i];
            var right = other._prerelease[i];
            var leftNumeric = IsNumeric(left);
            var rightNumeric = IsNumeric(right);
            var comparison = leftNumeric && rightNumeric
                ? CompareNumeric(left, right)
                : leftNumeric != rightNumeric
                    ? leftNumeric ? -1 : 1
                    : string.CompareOrdinal(left, right);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }

    int IComparable.CompareTo(object? obj) => obj is null || obj is SemanticVersion
        ? CompareTo((SemanticVersion?)obj)
        : throw new ArgumentException("只能比较 SemVer 版本号。", nameof(obj));

    public static bool operator ==(SemanticVersion? left, SemanticVersion? right) =>
        ReferenceEquals(left, right) || (left is not null && left.Equals(right));

    public static bool operator !=(SemanticVersion? left, SemanticVersion? right) => !(left == right);

    public bool Equals(SemanticVersion? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var part in _core.Concat(_prerelease))
        {
            hash.Add(part, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => _original;

    private static bool ValidIdentifiers(string text, bool prerelease) =>
        text.Split('.').All(part => part.Length > 0
            && part.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '-')
            && (!prerelease || !IsNumeric(part) || !HasLeadingZero(part)));

    private static bool IsNumeric(string text) => text.Length > 0 && text.All(c => c is >= '0' and <= '9');
    private static bool HasLeadingZero(string text) => text.Length > 1 && text[0] == '0';
    private static int CompareNumeric(string left, string right) =>
        left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right);
}
