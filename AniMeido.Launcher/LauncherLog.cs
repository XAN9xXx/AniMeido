using System.Text;

namespace AniMeido.Launcher;

internal sealed class LauncherLog(string path)
{
    public string Path { get; } = path;

    internal void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            if (File.Exists(Path) && new FileInfo(Path).Length > 1024 * 1024)
            {
                File.Move(Path, System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, "launcher.1.log"), overwrite: true);
            }
            File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}\r\n", new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 日志不可写/轮换失败不影响启动。
        }
    }
}
