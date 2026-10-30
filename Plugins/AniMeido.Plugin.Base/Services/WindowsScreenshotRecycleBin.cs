using Microsoft.VisualBasic.FileIO;

namespace AniMeido.Plugin.Base.Services;

internal sealed class WindowsScreenshotRecycleBin : IScreenshotRecycleBin
{
    public void MoveToRecycleBin(string path)
        => FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
}
