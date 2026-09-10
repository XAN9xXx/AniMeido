namespace AniMeido.Plugin.Player.Sources.Web;

internal static class HeaderNormalizer
{
    public static Dictionary<string, string> Merge(
        params IEnumerable<KeyValuePair<string, string>>?[] sources)
    {
        var result = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            if (source is null)
            {
                continue;
            }

            foreach (var (name, value) in source)
            {
                if (string.IsNullOrWhiteSpace(name)
                    || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                result[CanonicalizeName(name)] = value.Trim();
            }
        }

        return result;
    }

    public static string Redact(string name, string value)
        => IsSensitive(name) ? "<redacted>" : RedactUri(value);

    public static string RedactDiagnostic(string value)
        => System.Text.RegularExpressions.Regex.Replace(
            value,
            @"(https?://[^\s?]+)\?[^\s]+",
            "$1?<redacted>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

    private static string CanonicalizeName(string name)
        => name.Trim() switch
        {
            var value when value.Equals(
                "user-agent",
                StringComparison.OrdinalIgnoreCase) => "User-Agent",
            var value when value.Equals(
                "referer",
                StringComparison.OrdinalIgnoreCase) => "Referer",
            var value when value.Equals(
                "cookie",
                StringComparison.OrdinalIgnoreCase) => "Cookie",
            var value when value.Equals(
                "authorization",
                StringComparison.OrdinalIgnoreCase) => "Authorization",
            var value => value,
        };

    private static bool IsSensitive(string name)
        => name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Contains("token", StringComparison.OrdinalIgnoreCase);

    private static string RedactUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || string.IsNullOrEmpty(uri.Query))
        {
            return value;
        }

        return uri.GetLeftPart(UriPartial.Path) + "?<redacted>";
    }
}
