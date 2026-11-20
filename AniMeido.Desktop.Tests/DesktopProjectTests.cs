using System.Xml.Linq;
using Xunit;

namespace AniMeido.Desktop.Tests;

public sealed class DesktopProjectTests
{
    [Fact]
    public void BaseSolutionDoesNotBuildPlayerOrItsDedicatedTests()
    {
        var root = FindRepository();
        var projects = XDocument.Load(Path.Combine(root, "AniMeido.slnx")).Descendants("Project")
            .Select(node => (string)node.Attribute("Path")!).ToArray();
        Assert.Contains("AniMeido.PluginHost/AniMeido.PluginHost.csproj", projects);
        Assert.DoesNotContain(projects, name => name.Contains("AniMeido.Plugin.Player", StringComparison.OrdinalIgnoreCase));
        foreach (var project in projects)
        {
            Assert.DoesNotContain(XDocument.Load(Path.Combine(root, project)).Descendants("ProjectReference"),
                reference => ((string?)reference.Attribute("Include"))?.Contains("AniMeido.Plugin.Player", StringComparison.OrdinalIgnoreCase) == true);
        }
    }

    [Theory]
    [InlineData("AniMeido.Desktop")]
    [InlineData("Plugins/AniMeido.Plugin.Base.Desktop")]
    [Trait("Category", "Mechanism")]
    public void DesktopSourcesKeepInstructionalHoverTooltipsRemoved(string project)
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(FindRepository(), project), "*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".xaml", StringComparison.Ordinal))
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")))
            Assert.DoesNotContain("ToolTipService", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void SkiaHeadUsesUnoResourceGenerationWithoutTheNativePriPipeline()
    {
        var project = XDocument.Load(Path.Combine(FindRepository(),
            "AniMeido.Desktop", "AniMeido.Desktop.csproj"));
        Assert.Empty(project.Descendants("EnableCoreMrtTooling"));
        Assert.Empty(project.Descendants("WindowsAppSDKSelfContained"));
        Assert.DoesNotContain(project.Descendants("Target"), target =>
            (string?)target.Attribute("Name") == "CopyDesktopPri");
    }

    [Fact]
    public void WindowsHeadUsesUnoWithoutTheLegacyHead()
    {
        var root = FindRepository();
        var desktop = XDocument.Load(Path.Combine(root, "AniMeido.Desktop", "AniMeido.Desktop.csproj"));
        Assert.StartsWith("Uno.Sdk", (string?)desktop.Root!.Attribute("Sdk"));
        Assert.Equal("net10.0-desktop",
            desktop.Descendants("TargetFramework").Single().Value);
        var references = desktop.Descendants("ProjectReference")
            .Select(item => (string)item.Attribute("Include")!).ToArray();
        Assert.Contains(references, path => path.Contains("AniMeido.Plugin.Base.Desktop"));
        Assert.DoesNotContain(references, path => path.Contains("AniMeido.Plugin.Player"));
        Assert.False(File.Exists(Path.Combine(root, "AniMeido.App", "AniMeido.App.csproj")));
    }

    [Fact]
    public void DesktopPresentationKeepsTheBaseIdentityAndTheSharedBusinessCore()
    {
        var root = FindRepository();
        var project = XDocument.Load(Path.Combine(root, "Plugins",
            "AniMeido.Plugin.Base.Desktop", "AniMeido.Plugin.Base.Desktop.csproj"));
        Assert.Equal("AniMeido.Plugin.Base", project.Descendants("AssemblyName").Single().Value);
        Assert.Contains(project.Descendants("ProjectReference"),
            item => ((string?)item.Attribute("Include"))?.Contains("AniMeido.Plugin.Base.Core") == true);
        Assert.DoesNotContain(project.Descendants("ProjectReference"),
            item => ((string?)item.Attribute("Include"))?.Contains("AniMeido.Plugin.Player") == true);
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public void SearchResultsDisappearWhenTheExistingSearchStateReturnsToLanding()
    {
        var page = XDocument.Load(Path.Combine(FindRepository(), "Plugins",
            "AniMeido.Plugin.Base.Desktop", "Views", "GlobalSearchPage.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var results = page.Descendants().Single(element =>
            (string?)element.Attribute(xaml + "Name") == "SearchResultsLayout");

        var visibility = (string?)results.Attribute("Visibility") ?? string.Empty;
        Assert.Contains("ViewModel.HasSearched", visibility);
        Assert.Contains("OneWay", visibility);
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
            directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AniMeido.slnx")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("找不到 AniMeido.slnx。");
    }
}
