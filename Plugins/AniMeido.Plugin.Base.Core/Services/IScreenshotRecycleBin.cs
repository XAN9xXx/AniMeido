namespace AniMeido.Plugin.Base.Services;

/// <summary>将截图送入系统回收站，不以永久删除代替。</summary>
public interface IScreenshotRecycleBin
{
    void MoveToRecycleBin(string path);
}
