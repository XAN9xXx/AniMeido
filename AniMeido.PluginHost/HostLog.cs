namespace AniMeido.PluginHost;

internal static class HostLog
{
    private static readonly object Gate = new();
    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AniMeido",
        "logs",
        "plugin-host.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
                File.AppendAllText(
                    LogFile,
                    $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[PluginHost] Unable to write log: {ex.Message}");
        }
    }
}
