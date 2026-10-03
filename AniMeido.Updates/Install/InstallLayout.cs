namespace AniMeido.Updates.Install;

public static class InstallLayout
{
    public const string AppFileName = "AniMeido.App.exe";

    public static string VersionDirectory(string root, string version)
    {
        SemanticVersion.Parse(version);
        return Path.Combine(Path.GetFullPath(root), "app-" + version);
    }

    public static string ExecutablePath(string root, string version)
        => Path.Combine(VersionDirectory(root, version), AppFileName);

    public static bool IsValidVersion(string root, string? version)
        => SemanticVersion.TryParse(version, out _)
            && !version!.EndsWith(".staging", StringComparison.OrdinalIgnoreCase)
            && File.Exists(ExecutablePath(root, version!));

    public static bool TryGetDirectoryVersion(string directoryName, out string? version)
    {
        version = null;
        if (!directoryName.StartsWith("app-", StringComparison.Ordinal)
            || directoryName.EndsWith(".staging", StringComparison.OrdinalIgnoreCase)
            || !SemanticVersion.TryParse(directoryName[4..], out _))
        {
            return false;
        }

        version = directoryName[4..];
        return true;
    }
}
