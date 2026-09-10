using System.Text.Json;
using AniMeido.Plugin.Player.Sources.Packages;

namespace AniMeido.Plugin.Player.Sources.Rules;

internal static class SourceRuleLoader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static IEnumerable<IOnlineAnimeSource> Load(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        foreach (var path in GetRulePaths())
        {
            IOnlineAnimeSource? provider;
            try
            {
                var json = File.ReadAllText(path);
                using var document = JsonDocument.Parse(json);
                var kind = document.RootElement.TryGetProperty(
                    "kind",
                    out var kindElement)
                    ? kindElement.GetString()
                    : "api";
                provider = string.Equals(
                    kind,
                    "static",
                    StringComparison.Ordinal)
                    ? CreateStaticProvider(json)
                    : CreateApiProvider(httpClient, json);
            }
#pragma warning disable CA1031 // One malformed rule must not disable other sources.
            catch (Exception)
            {
                continue;
            }
#pragma warning restore CA1031

            if (provider is null)
            {
                continue;
            }

            yield return provider;
        }
    }

    private static IOnlineAnimeSource? CreateApiProvider(
        HttpClient httpClient,
        string json)
    {
        var rule = JsonSerializer.Deserialize<ApiSourceRule>(
            json,
            SerializerOptions);
        return rule is null
            ? null
            : new ApiRuleSourceProvider(httpClient, rule);
    }

    private static IOnlineAnimeSource? CreateStaticProvider(string json)
    {
        var rule = JsonSerializer.Deserialize<StaticSourceRule>(
            json,
            SerializerOptions);
        return rule is null ? null : new StaticSourceProvider(rule);
    }

    private static IReadOnlyList<string> GetRulePaths()
    {
        try
        {
            var sourceDirectory = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "AniMeido",
                "Player",
                "Sources");
            Directory.CreateDirectory(sourceDirectory);
            var looseRules = Directory.EnumerateFiles(
                sourceDirectory,
                "*.animeido-source.json",
                SearchOption.TopDirectoryOnly);
            var packagedRules = SourcePackageDiscovery
                .EnumerateEnabledPackageDirectories(sourceDirectory)
                .SelectMany(directory => Directory.EnumerateFiles(
                    directory,
                    "*.animeido-source.json",
                    SearchOption.AllDirectories));
            return looseRules.Concat(packagedRules).ToArray();
        }
#pragma warning disable CA1031 // Source discovery failure must not break BasePlugin.
        catch (Exception)
        {
            return [];
        }
#pragma warning restore CA1031
    }
}
