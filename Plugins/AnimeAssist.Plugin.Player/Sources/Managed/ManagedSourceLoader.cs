using System.Text.Json;
using AniMeido.Plugin.Player.Sources.Packages;

namespace AniMeido.Plugin.Player.Sources.Managed;

internal static class ManagedSourceLoader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static IEnumerable<IOnlineAnimeSource> Load()
    {
        foreach (var manifestPath in GetManifestPaths())
        {
            IOnlineAnimeSource? source;
            try
            {
                source = LoadOne(manifestPath);
            }
#pragma warning disable CA1031 // One source package must not disable other sources.
            catch (Exception)
            {
                continue;
            }
#pragma warning restore CA1031

            if (source is not null)
            {
                yield return source;
            }
        }
    }

    private static IOnlineAnimeSource LoadOne(string manifestPath)
    {
        using var stream = File.OpenRead(manifestPath);
        var manifest = JsonSerializer.Deserialize<ManagedSourceManifest>(
            stream,
            SerializerOptions)
            ?? throw new InvalidDataException("代码源缺少 manifest。");
        ValidateManifest(manifest);

        var sourceDirectory = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidDataException("代码源目录无效。");
        var assemblyPath = ResolveSafePath(
            sourceDirectory,
            manifest.EntryAssembly);
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException("代码源入口程序集不存在。", assemblyPath);
        }

        var loadContext = new ManagedSourceLoadContext(sourceDirectory);
        var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
        var candidates = assembly
            .GetExportedTypes()
            .Where(type =>
                typeof(IOnlineAnimeSource).IsAssignableFrom(type)
                && type.IsClass
                && !type.IsAbstract)
            .ToArray();
        var sourceType = string.IsNullOrWhiteSpace(manifest.EntryType)
            ? candidates.SingleOrDefault()
            : candidates.SingleOrDefault(type => string.Equals(
                type.FullName,
                manifest.EntryType,
                StringComparison.Ordinal));
        if (sourceType is null)
        {
            throw new InvalidDataException(
                "代码源必须包含唯一、公开且可创建的 IOnlineAnimeSource。");
        }

        if (Activator.CreateInstance(sourceType) is not IOnlineAnimeSource source)
        {
            throw new InvalidDataException("无法创建代码源入口类型。");
        }

        if (!string.Equals(source.Id, manifest.Id, StringComparison.Ordinal)
            || !string.Equals(
                source.DisplayName,
                manifest.DisplayName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "代码源 manifest 与运行时身份不一致。");
        }

        return new ManagedSourceProxy(loadContext, source);
    }

    private static IReadOnlyList<string> GetManifestPaths()
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
            var legacyManifests = Directory.EnumerateDirectories(
                    sourceDirectory,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .Where(directory => !string.Equals(
                    Path.GetFileName(directory),
                    "Packages",
                    StringComparison.OrdinalIgnoreCase))
                .Select(directory => Path.Combine(directory, "source.json"))
                .Where(File.Exists);
            var packagedManifests = SourcePackageDiscovery
                .EnumerateEnabledPackageDirectories(sourceDirectory)
                .SelectMany(directory => Directory.EnumerateFiles(
                    directory,
                    "source.json",
                    SearchOption.AllDirectories));
            return legacyManifests.Concat(packagedManifests).ToArray();
        }
#pragma warning disable CA1031 // Source discovery failure must not break BasePlugin.
        catch (Exception)
        {
            return [];
        }
#pragma warning restore CA1031
    }

    private static string ResolveSafePath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("代码源入口必须使用相对路径。");
        }

        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!candidate.StartsWith(
            normalizedRoot,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("代码源入口越过了源包目录。");
        }

        return candidate;
    }

    private static void ValidateManifest(ManagedSourceManifest manifest)
    {
        if (manifest.FormatVersion != 1
            || string.IsNullOrWhiteSpace(manifest.Id)
            || string.IsNullOrWhiteSpace(manifest.DisplayName)
            || string.IsNullOrWhiteSpace(manifest.EntryAssembly))
        {
            throw new InvalidDataException("代码源 manifest 缺少必填字段。");
        }
    }
}
