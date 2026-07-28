using AniMeido.Plugin.Player.Sources.Animeko;
using AniMeido.Plugin.Player.Sources.EasyBangumi;
using AniMeido.Plugin.Player.Sources.Packages;
using AniMeido.Plugin.Player.Sources.Web;
using System.Text.Json;

namespace AniMeido.Plugin.Player.Sources.Subscriptions;

internal static class SubscriptionSourceLoader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static IEnumerable<IOnlineAnimeSource> Load(
        HttpClient httpClient,
        WebMediaResolver webResolver,
        EasyPreferenceStore preferenceStore)
    {
        var sourcesDirectory = SourcePackageInstaller.GetSourcesDirectory();
        foreach (var directory in SourcePackageDiscovery
            .EnumerateEnabledPackageDirectories(sourcesDirectory))
        {
            IOnlineAnimeSource? source = null;
            try
            {
                var manifest = ReadManifest(directory);
                if (manifest.FormatVersion != 2)
                {
                    continue;
                }

                var entryPath = ResolveSafePath(directory, manifest.EntryFile);
                var content = File.ReadAllText(entryPath);
                source = manifest.SourceKind switch
                {
                    "animeko-web-selector" => CreateAnimeko(
                        manifest,
                        content,
                        httpClient,
                        webResolver),
                    "easybangumi-js" => new EasyBangumiSource(
                        manifest.Id,
                        manifest.DisplayName,
                        content,
                        httpClient,
                        webResolver,
                        preferenceStore),
                    _ => null,
                };
            }
#pragma warning disable CA1031 // Invalid subscription packages do not hide valid sources.
            catch (Exception)
            {
            }
#pragma warning restore CA1031

            if (source is not null)
            {
                yield return source;
            }
        }
    }

    private static IOnlineAnimeSource CreateAnimeko(
        SourcePackageManifest manifest,
        string content,
        HttpClient httpClient,
        WebMediaResolver webResolver)
    {
        var definition = JsonSerializer.Deserialize<AnimekoSourceDefinition>(
            content,
            SerializerOptions)
            ?? throw new InvalidDataException("ani-subs 源配置为空。");
        return new AnimekoWebSource(
            manifest.Id,
            httpClient,
            webResolver,
            definition);
    }

    private static SourcePackageManifest ReadManifest(string directory)
    {
        using var stream = File.OpenRead(Path.Combine(
            directory,
            "source-package.json"));
        var manifest = JsonSerializer.Deserialize<SourcePackageManifest>(
            stream,
            SerializerOptions)
            ?? throw new InvalidDataException("订阅源包清单无效。");
        SourcePackageInstaller.ValidateManifest(manifest);
        return manifest;
    }

    private static string ResolveSafePath(string root, string relativePath)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!path.StartsWith(
            normalizedRoot,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("订阅源入口越过包目录。");
        }

        return path;
    }
}
