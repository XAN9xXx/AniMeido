using System.Xml.Linq;
using Xunit;

namespace AniMeido.Desktop.Tests;

[Trait("Category", "Mechanism")]
public sealed class DesktopSectionNavigationWiringTests
{
    [Fact]
    public void TheApplicationLoadsTheWindowsNavigationTemplate()
    {
        var app = Read("AniMeido.Desktop", "App.xaml");
        Assert.Contains(app.Descendants(), element =>
            element.Name.LocalName == "DesktopWindowsSectionNavigation");
    }

    [Fact]
    public void ArchiveTabsShareTheUnderlinedNavigationRole()
    {
        var page = Read("Plugins", "AniMeido.Plugin.Base.Desktop", "Views", "ArchivePage.xaml");
        var tabs = page.Descendants().Where(element => element.Name.LocalName == "ToggleButton"
            && (string?)element.Attribute("Click") == "OnSectionButtonClick").ToArray();

        Assert.Equal(4, tabs.Length);
        Assert.All(tabs, tab => Assert.Equal("{StaticResource DesktopUnderlinedSectionTabStyle}",
            (string?)tab.Attribute("Style")));
    }

    private static XDocument Read(params string[] path)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AniMeido.slnx")))
                return XDocument.Load(Path.Combine([directory.FullName, .. path]));
        throw new DirectoryNotFoundException("找不到 AniMeido.slnx。");
    }
}
