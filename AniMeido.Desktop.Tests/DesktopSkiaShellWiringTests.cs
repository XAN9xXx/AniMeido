using Xunit;

namespace AniMeido.Desktop.Tests;

[Trait("Category", "Mechanism")]
public sealed class DesktopSkiaShellWiringTests
{
    [Fact]
    public void ScreenshotFeedbackUsesImplementedMoveAndResizeMethods()
    {
        var source = Read("Plugins/AniMeido.Plugin.Base.Desktop/Views/CaptureFeedbackWindow.cs");
        Assert.Contains("DesktopShellServices.GetWindowHandle(this)", source);
        Assert.Contains("DesktopShellServices.GetWorkArea(handle)", source);
        Assert.Contains("AppWindow.Move(", source);
        Assert.Contains("AppWindow.Resize(", source);
        Assert.Contains("AppWindow.Show(activateWindow: false)", source);
    }

    [Fact]
    public void FilesAndFoldersUseTheWindowsShellOnSkia()
    {
        var archive = Read("Plugins/AniMeido.Plugin.Base.Desktop/Views/ArchivePage.Screenshots.cs");
        Assert.Contains("DesktopShellServices.OpenPath(item.FilePath)", archive);
        var settings = Read("Plugins/AniMeido.Plugin.Base.Desktop/Views/DataManagementSettingsPage.xaml.cs");
        Assert.Contains("OpenDirectoryAsync(Path.GetDirectoryName(_paths.DatabasePath)!", settings);
        Assert.Contains("OpenDirectoryAsync(_paths.BackupDirectory)", settings);
        Assert.Contains("OpenDirectoryAsync(_paths.LogDirectory)", settings);
        Assert.Contains("DesktopShellServices.OpenPath(path)", settings);
        var shell = Read("Plugins/AniMeido.Plugin.Base.Desktop/Services/DesktopShellServices.cs");
        Assert.Contains("UseShellExecute = true", shell);
        Assert.Contains("FileName = path", shell);
        Assert.Contains("MonitorFromWindow", shell);
        Assert.Contains("MonitorFromPoint", shell);
        Assert.Contains("GetMonitorInfo", shell);
    }

    private static string Read(string path)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AniMeido.slnx")))
                return File.ReadAllText(Path.Combine(directory.FullName, path));
        throw new DirectoryNotFoundException("找不到 AniMeido.slnx。");
    }
}
