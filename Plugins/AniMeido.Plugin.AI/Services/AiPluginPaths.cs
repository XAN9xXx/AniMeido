namespace AniMeido.Plugin.AI.Services;

internal sealed class AiPluginPaths
{
    public AiPluginPaths()
        : this(null)
    {
    }

    internal AiPluginPaths(string? rootDirectory)
    {
        RootDirectory = rootDirectory ?? DefaultRootDirectory;
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(ExportDirectory);
    }

    internal static string DefaultRootDirectory => Path.Combine(AppDataRoots.Local, "Plugins", "AniMeido.Plugin.AI");

    public string RootDirectory { get; }

    public string SettingsPath => Path.Combine(RootDirectory, "settings.json");

    public string SecretsPath => Path.Combine(RootDirectory, "secrets.dat");

    public string ConversationsPath =>
        Path.Combine(RootDirectory, "conversations.db");

    public string ExportDirectory => Path.Combine(RootDirectory, "exports");
}
