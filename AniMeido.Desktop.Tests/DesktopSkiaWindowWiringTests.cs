using Xunit;

namespace AniMeido.Desktop.Tests;

[Trait("Category", "Mechanism")]
public sealed class DesktopSkiaWindowWiringTests
{
    [Theory]
    [InlineData("AniMeido.Desktop/App.xaml.cs", "_mainAppWindow = _window.AppWindow")]
    [InlineData("AniMeido.Desktop/Services/TitleBarHelper.cs", "window.AppWindow.SetIcon(iconPath)")]
    public void SkiaUsesTheWindowOwnedAppWindow(string path, string expected)
    {
        var source = Read(path);
        Assert.Contains(expected, source);
    }

    [Fact]
    public void SkiaActivationRetainsTheRealNativeWindowForTrayAndShortcuts()
    {
        var service = Read("AniMeido.Desktop/Services/AppWindowActivationService.cs");
        Assert.Contains("DesktopNativeWindow.GetHandle(window)", service);
        Assert.Contains("DesktopNativeWindow.Hide(_windowHandle)", service);
        Assert.Contains("DesktopNativeWindow.Show(_windowHandle)", service);
        var native = Read("AniMeido.Desktop/Services/DesktopNativeWindow.cs");
        Assert.Contains("WindowHelper.GetNativeWindow(window)", native);
        Assert.Contains("Win32NativeWindow", native);
        Assert.Contains(".Hwnd", native);
        Assert.Contains("IsIconic", native);
        Assert.Contains("ShowWindow", native);
    }

    [Fact]
    public void SkiaPluginPickerUsesTheUnoNativeFilePickerAndKeepsItsPackageFilter()
    {
        var source = Read("AniMeido.Desktop/Views/AppSettingsPage.xaml.cs");
        Assert.Contains("using Windows.Storage.Pickers;", source);
        Assert.Contains("new FileOpenPicker();", source);
        Assert.Contains("picker.FileTypeFilter.Add(\".animeido-plugin\")", source);
        Assert.Contains("IsCurrent(visit)", source);
    }

    [Fact]
    public void ShellCancelsClosingUntilServiceShutdownHasCompleted()
    {
        var source = Read("AniMeido.Desktop/App.xaml.cs");
        Assert.Contains("_mainAppWindow.Closing += OnMainAppWindowClosing", source);
        Assert.Contains("args.Cancel = true", source);
        Assert.Contains(".HideMainWindow()", source);
        Assert.Contains("await StopApplicationServicesAsync(provider, restorePreparation)", source);
        Assert.Contains("_window?.QueueFinalClose()", source);
    }

    [Fact]
    public void DesktopShellOwnsItsNavigationTemplateAndWindow()
    {
        var source = Read("AniMeido.Desktop/MainWindow.xaml.cs");
        Assert.Contains("TitleBarHelper.UpdateButtonColors(AppWindow,", source);
        Assert.DoesNotContain("VisualTreeHelper", source);
        Assert.DoesNotContain("ApplyToSubtree", source);
        Assert.DoesNotContain("NavigationView", Read("AniMeido.Desktop/MainWindow.xaml"));
    }

    [Fact]
    public void NavigationUsesNeutralFeedbackForAllSelectedStatesAndSmoothBackgrounds()
    {
        var window = System.Xml.Linq.XDocument.Parse(Read("AniMeido.Desktop/Themes/DesktopShellResources.xaml"));
        foreach (var state in new[] { "PointerOver", "Pressed" })
        {
            Assert.Contains(window.Descendants().SelectMany(element => element.Attributes()), attribute =>
                attribute.Value == "{ThemeResource DesktopNavigationSelected" + state + "Brush}");
        }
        Assert.Contains("DesktopButtonSurfaceMotion.Enable", Read("AniMeido.Desktop/Themes/DesktopShellResources.xaml"));
    }

    [Theory]
    [InlineData("AniMeido.Desktop/MainWindow.xaml.cs", "_migrationBackupDirectory")]
    [InlineData("AniMeido.Desktop/Views/AppSettingsPage.Updates.cs", "directory")]
    public void BackupFolderActionsUseTheWindowsShellOnSkia(string path, string argument)
    {
        Assert.Contains($"await DesktopNativeWindow.OpenFolderAsync({argument})", Read(path));
        var native = Read("AniMeido.Desktop/Services/DesktopNativeWindow.cs");
        Assert.Contains("UseShellExecute = true", native);
    }

    private static string Read(string path)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AniMeido.slnx")))
                return File.ReadAllText(Path.Combine(directory.FullName, path));
        throw new DirectoryNotFoundException("找不到 AniMeido.slnx。");
    }
}
