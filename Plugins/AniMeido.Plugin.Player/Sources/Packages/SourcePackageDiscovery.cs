namespace AniMeido.Plugin.Player.Sources.Packages;

internal static class SourcePackageDiscovery
{
    public const string DisabledMarkerName = ".disabled";

    public static IEnumerable<string> EnumerateEnabledPackageDirectories(
        string sourcesDirectory)
    {
        var packagesDirectory = Path.Combine(sourcesDirectory, "Packages");
        if (!Directory.Exists(packagesDirectory))
        {
            return [];
        }

        return Directory.EnumerateDirectories(
                packagesDirectory,
                "*",
                SearchOption.TopDirectoryOnly)
            .Where(IsPackageDirectory)
            .Where(directory => !File.Exists(Path.Combine(
                directory,
                DisabledMarkerName)))
            .ToArray();
    }

    public static bool IsPackageDirectory(string directory)
    {
        var name = Path.GetFileName(directory);
        return !string.IsNullOrWhiteSpace(name)
            && !name.StartsWith(".", StringComparison.Ordinal)
            && !name.Contains(".backup-", StringComparison.OrdinalIgnoreCase)
            && !name.Contains(".remove-", StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(directory, "source-package.json"));
    }
}
