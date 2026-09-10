namespace AniMeido.App.Services;

internal sealed class PluginInstallationPaths
{
    public PluginInstallationPaths(string rootDirectory)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        InstalledDirectory = Path.Combine(RootDirectory, "installed");
        StagingDirectory = Path.Combine(RootDirectory, "staging");
        QuarantineDirectory = Path.Combine(RootDirectory, "quarantine");
        StateFile = Path.Combine(RootDirectory, "state.json");
        StateBackupFile = Path.Combine(RootDirectory, "state.backup.json");
    }

    public string RootDirectory { get; }

    public string InstalledDirectory { get; }

    public string StagingDirectory { get; }

    public string QuarantineDirectory { get; }

    public string StateFile { get; }

    public string StateBackupFile { get; }

    public static PluginInstallationPaths CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return new PluginInstallationPaths(
            Path.Combine(localAppData, "AniMeido", "Plugins"));
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(InstalledDirectory);
        Directory.CreateDirectory(StagingDirectory);
        Directory.CreateDirectory(QuarantineDirectory);
    }

    public string GetPluginDirectory(string pluginId)
        => PluginPackageVerifier.ResolveSafePath(InstalledDirectory, pluginId);

    public string GetVersionDirectory(string pluginId, string version)
        => PluginPackageVerifier.ResolveSafePath(
            GetPluginDirectory(pluginId),
            Path.Combine("versions", version));
}
