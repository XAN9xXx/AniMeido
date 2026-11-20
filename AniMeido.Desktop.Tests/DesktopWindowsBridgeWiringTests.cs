using System.Xml.Linq;
using Xunit;

namespace AniMeido.Desktop.Tests;

[Trait("Category", "Mechanism")]
public sealed class DesktopWindowsBridgeWiringTests
{
    [Fact]
    public void WindowsServicesExposeAPlainNetLibraryWithoutAWinUiPackage()
    {
        var project = ReadProject();
        Assert.Contains(project.Descendants(), element => element.Name.LocalName == "TargetFramework" && element.Value == "net10.0");
        Assert.DoesNotContain(project.Descendants(), element => element.Name.LocalName == "UseWinUI");
        Assert.DoesNotContain(project.Descendants(), element => element.Name.LocalName == "PackageReference"
            && ((string?)element.Attribute("Include"))?.Contains("WindowsAppSDK", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void WindowsBridgeOwnsCaptureAndNotificationImplementations()
    {
        var assembly = typeof(AniMeido.App.Services.WindowsAppNotificationService).Assembly;
        Assert.Equal("AniMeido.Platform.Windows", assembly.GetName().Name);
        Assert.Same(assembly, typeof(AniMeido.App.Services.ForegroundWindowCaptureService).Assembly);
    }

    private static XDocument ReadProject()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AniMeido.slnx")))
                return XDocument.Load(Path.Combine(directory.FullName, "AniMeido.Platform.Windows", "AniMeido.Platform.Windows.csproj"));
        throw new DirectoryNotFoundException("找不到 AniMeido.slnx。");
    }
}
