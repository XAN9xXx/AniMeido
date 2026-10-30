using System.Net;
using System.Text.RegularExpressions;

namespace AniMeido.Plugin.Player.Sources.Web;

internal static class WebMediaUrlParser
{
    internal static Uri? FindMedia(
        string content,
        WebResolutionRequest request,
        Uri baseUri)
    {
        var matched = FindFirstUrl(
            content,
            request.VideoUrlPattern,
            baseUri);
        return matched is not null
            && TryExtractEmbeddedMediaUri(
                matched,
                request.VideoUrlPattern,
                baseUri,
                out var embedded)
                    ? embedded
                    : matched;
    }

    internal static Uri? FindNestedPageUri(
        string content,
        string pattern,
        Uri baseUri)
    {
        var iframePattern = new Regex(
            """<iframe\b[^>]*\bsrc\s*=\s*["'](?<v>[^"']+)["']""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2));
        foreach (Match match in iframePattern.Matches(content))
        {
            var value = WebUtility.HtmlDecode(match.Groups["v"].Value);
            if (Uri.TryCreate(baseUri, value, out var iframeUri)
                && iframeUri.Scheme is "http" or "https"
                && !IsIgnoredMediaUrl(iframeUri))
            {
                return iframeUri;
            }
        }

        return FindFirstUrl(content, pattern, baseUri);
    }

    private static Uri? FindFirstUrl(
        string content,
        string pattern,
        Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        var matches = Regex.Matches(
            content,
            pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2));
        foreach (Match match in matches)
        {
            var value = match.Groups["v"].Success
                ? match.Groups["v"].Value
                : match.Value;
            value = value
                .Replace("\\/", "/", StringComparison.Ordinal)
                .Replace("&amp;", "&", StringComparison.Ordinal);
            if (value.StartsWith("url=", StringComparison.OrdinalIgnoreCase))
            {
                value = value[4..];
            }

            value = Uri.UnescapeDataString(value);
            if (Uri.TryCreate(value, UriKind.Absolute, out var absolute)
                && absolute.Scheme is "http" or "https"
                && !IsIgnoredMediaUrl(absolute))
            {
                return absolute;
            }

            if (Uri.TryCreate(baseUri, value, out var relative)
                && relative.Scheme is "http" or "https"
                && !IsIgnoredMediaUrl(relative))
            {
                return relative;
            }
        }

        return null;
    }

    private static bool TryMatchUrl(
        string value,
        string pattern,
        Uri baseUri,
        out Uri mediaUri)
    {
        var matched = FindFirstUrl(value, pattern, baseUri);
        if (matched is not null
            && TryExtractEmbeddedMediaUri(
                matched,
                pattern,
                baseUri,
                out var embedded))
        {
            mediaUri = embedded;
            return true;
        }

        mediaUri = matched!;
        return matched is not null;
    }

    internal static bool TryMatchObservedMediaUrl(
        string value,
        string pattern,
        Uri baseUri,
        out Uri mediaUri)
    {
        if (!TryMatchUrl(value, pattern, baseUri, out mediaUri))
        {
            return false;
        }

        if (!Uri.TryCreate(baseUri, value, out var observedUri))
        {
            return true;
        }

        return observedUri.Scheme.Equals(
                mediaUri.Scheme,
                StringComparison.OrdinalIgnoreCase)
            && observedUri.Host.Equals(
                mediaUri.Host,
                StringComparison.OrdinalIgnoreCase)
            && observedUri.Port == mediaUri.Port
            && observedUri.AbsolutePath.Equals(
                mediaUri.AbsolutePath,
                StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryExtractEmbeddedMediaUri(
        Uri wrapperUri,
        string pattern,
        Uri baseUri,
        out Uri mediaUri)
    {
        mediaUri = null!;
        if (string.IsNullOrWhiteSpace(wrapperUri.Query))
        {
            return false;
        }

        foreach (var pair in wrapperUri.Query.TrimStart('?').Split('&'))
        {
            var separator = pair.IndexOf('=');
            if (separator < 0 || separator == pair.Length - 1)
            {
                continue;
            }

            var value = pair[(separator + 1)..];
            for (var decode = 0; decode < 3; decode++)
            {
                var decoded = WebUtility.UrlDecode(value);
                if (string.Equals(decoded, value, StringComparison.Ordinal))
                {
                    break;
                }

                value = decoded;
            }

            var matched = FindFirstUrl(value, pattern, baseUri);
            if (matched is not null
                && !string.Equals(
                    matched.AbsoluteUri,
                    wrapperUri.AbsoluteUri,
                    StringComparison.Ordinal))
            {
                mediaUri = matched;
                return true;
            }
        }

        return false;
    }

    internal static bool TryCreateMediaUri(
        string value,
        Uri baseUri,
        out Uri mediaUri)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out mediaUri!)
            && !Uri.TryCreate(baseUri, value, out mediaUri!))
        {
            return false;
        }

        return mediaUri.Scheme is "http" or "https"
            && !IsIgnoredMediaUrl(mediaUri);
    }

    internal static bool IsIgnoredMediaUrl(Uri uri)
    {
        var value = uri.AbsoluteUri;
        return value.Contains("googleads", StringComparison.OrdinalIgnoreCase)
            || value.Contains(
                "googlesyndication",
                StringComparison.OrdinalIgnoreCase)
            || value.Contains(
                "adtrafficquality",
                StringComparison.OrdinalIgnoreCase)
            || value.Contains("doubleclick", StringComparison.OrdinalIgnoreCase);
    }
}
