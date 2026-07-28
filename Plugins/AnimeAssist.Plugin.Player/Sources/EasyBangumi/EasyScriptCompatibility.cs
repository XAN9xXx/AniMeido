using System.Text.RegularExpressions;

namespace AniMeido.Plugin.Player.Sources.EasyBangumi;

internal static partial class EasyScriptCompatibility
{
    private static readonly HashSet<string> SupportedInjections =
    [
        "Inject_NetworkHelper",
        "Inject_OkhttpHelper",
        "Inject_PreferenceHelper",
        "Inject_RenderHelper",
        "Inject_WebProxyProvider",
        "Inject_WebViewHelperV2",
    ];
    private static readonly HashSet<string> SupportedConstructors =
    [
        "Array",
        "ArrayList",
        "Date",
        "Episode",
        "Error",
        "HashMap",
        "JSONObject",
        "JsVideoStrategy",
        "MainTab",
        "Map",
        "Object",
        "Pair",
        "ParserException",
        "PlayerInfo",
        "PlayLine",
        "Promise",
        "RegExp",
        "Set",
        "SourcePreference.Edit",
        "SubTab",
        "URL",
    ];
    private static readonly HashSet<string> SupportedXPathMethods =
    [
        "attr",
        "attrSelf",
        "firstImage",
        "nodes",
        "text",
        "textSelf",
        "title",
    ];
    private static readonly HashSet<string> SupportedOkhttpMethods =
    [
        "get",
        "postFromBody",
    ];

    public static void Validate(string script)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        RequireFunction(script, "SearchComponent_search");
        RequireFunction(script, "DetailedComponent_getDetailed");
        RequireFunction(script, "PlayComponent_getPlayInfo");
        RejectUnknown(
            InjectionRegex(),
            script,
            SupportedInjections,
            "注入对象");
        RejectUnknown(
            ConstructorRegex(),
            script,
            SupportedConstructors,
            "构造函数");
        RejectUnknown(
            XPathMethodRegex(),
            script,
            SupportedXPathMethods,
            "XPathUtils 方法");
        RejectUnknown(
            OkhttpMethodRegex(),
            script,
            SupportedOkhttpMethods,
            "OkhttpUtils 方法");
    }

    private static void RequireFunction(string script, string name)
    {
        if (!Regex.IsMatch(
                script,
                $@"function\s+{Regex.Escape(name)}\s*\(",
                RegexOptions.CultureInvariant))
        {
            throw new InvalidDataException(
                $"EasyBangumi 脚本缺少 {name}。");
        }
    }

    private static void RejectUnknown(
        Regex regex,
        string script,
        IReadOnlySet<string> supported,
        string category)
    {
        var unknown = regex.Matches(script)
            .Select(match => match.Groups["name"].Value)
            .Where(name => !supported.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (unknown.Length > 0)
        {
            throw new InvalidDataException(
                $"EasyBangumi 脚本使用未支持的{category}："
                + string.Join(", ", unknown));
        }
    }

    [GeneratedRegex(
        @"\b(?<name>Inject_[A-Za-z0-9_]+)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex InjectionRegex();

    [GeneratedRegex(
        @"\bnew\s+(?<name>[A-Za-z_$][A-Za-z0-9_$.]*)",
        RegexOptions.CultureInvariant)]
    private static partial Regex ConstructorRegex();

    [GeneratedRegex(
        @"\bXPathUtils\.(?<name>[A-Za-z_$][A-Za-z0-9_$]*)",
        RegexOptions.CultureInvariant)]
    private static partial Regex XPathMethodRegex();

    [GeneratedRegex(
        @"\bOkhttpUtils\.(?<name>[A-Za-z_$][A-Za-z0-9_$]*)",
        RegexOptions.CultureInvariant)]
    private static partial Regex OkhttpMethodRegex();
}
