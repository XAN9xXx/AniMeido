using Xunit;

namespace AniMeido.Desktop.Tests;

[Trait("Category", "Mechanism")]
public sealed class DesktopSkiaShellWiringTests
{
    [Theory]
    [InlineData("TodayPage.PlanStack.cs")]
    [InlineData("RecommendationPage.TagStack.cs")]
    public void ExpandedDecksUseTheNativeMonitorWorkAreaOnSkia(string file)
    {
        var source = Read("Plugins/AniMeido.Plugin.Base/Views/" + file);
        Assert.Contains("#if UNO_SKIA", source);
        Assert.Contains("DesktopShellServices.TryGetWorkAreaAtClientPoint", source);
        Assert.Contains("InputActivationChanged +=", source);
        Assert.Contains("InputActivationChanged -=", source);
    }

    [Fact]
    public void ScreenshotFeedbackUsesImplementedMoveAndResizeMethods()
    {
        var source = Read("Plugins/AniMeido.Plugin.Base/Views/CaptureFeedbackWindow.cs");
        Assert.Contains("DesktopShellServices.GetWindowHandle(this)", source);
        Assert.Contains("DesktopShellServices.GetWorkArea(handle)", source);
        Assert.Contains("appWindow.Move(", source);
        Assert.Contains("appWindow.Resize(", source);
        Assert.Contains("appWindow.Show(activateWindow: false)", source);
    }

    [Fact]
    public void FilesAndFoldersUseTheWindowsShellOnSkia()
    {
        var archive = Read("Plugins/AniMeido.Plugin.Base/Views/ArchivePage.xaml.cs");
        Assert.Contains("DesktopShellServices.OpenPath(item.FilePath)", archive);
        var settings = Read("Plugins/AniMeido.Plugin.Base/Views/DataManagementSettingsPage.xaml.cs");
        Assert.Contains("OpenDirectoryAsync(dir)", settings);
        Assert.Contains("OpenDirectoryAsync(_paths.BackupDirectory)", settings);
        Assert.Contains("OpenDirectoryAsync(_paths.LogDirectory)", settings);
        Assert.Contains("DesktopShellServices.OpenPath(directory)", settings);
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
