using System.Text.Json;
using AniMeido.Plugin.Player.Sources.Packages;
using Microsoft.Extensions.Logging;

namespace AniMeido.Plugin.Player.Sources.Rules;

internal static class SourceRuleLoader
{
    internal static string SourcesDirectory => Path.Combine(AppDataRoots.Local, "Player", "Sources");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static IEnumerable<IOnlineAnimeSource> Load(HttpClient httpClient, ILogger? logger = null)
        => Load(httpClient, SourcesDirectory, logger);

    internal static IEnumerable<IOnlineAnimeSource> Load(
        HttpClient httpClient, string sourcesDirectory, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        foreach (var path in GetRulePaths(sourcesDirectory, logger))
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
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "无法加载播放源规则，已跳过：{Path}", path);
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

    private static IReadOnlyList<string> GetRulePaths(string sourceDirectory, ILogger? logger)
    {
        try
        {
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
#pragma warning disable CA1031 // Source discovery failure must not break PlayerPlugin.
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "无法扫描播放源规则，本次按空目录处理：{Directory}", sourceDirectory);
            return [];
        }
#pragma warning restore CA1031
    }
}
