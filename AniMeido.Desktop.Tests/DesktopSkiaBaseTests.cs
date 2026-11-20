using System.Xml.Linq;
using Xunit;

namespace AniMeido.Desktop.Tests;

public sealed class DesktopSkiaBaseTests
{
    [Fact]
    public void RepositoryProjectsDoNotBuildTheNativeWinUiFramework()
    {
        var root = RepositoryRoot();
        var projects = XDocument.Load(Path.Combine(root, "AniMeido.slnx")).Descendants("Project")
            .Select(node => Path.Combine(root, (string)node.Attribute("Path")!))
            .Concat(Directory.EnumerateDirectories(root).SelectMany(dir => Directory.EnumerateFiles(dir, "*.csproj")))
            .Concat(Directory.EnumerateDirectories(Path.Combine(root, "Plugins"))
                .SelectMany(dir => Directory.EnumerateFiles(dir, "*.csproj")))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var file in projects)
        {
            var project = XDocument.Load(file);
            Assert.False(project.Descendants("UseWinUI").Any(node => node.Value == "true")
                || project.Descendants("PackageReference").Any(node =>
                    (string?)node.Attribute("Include") == "Microsoft.WindowsAppSDK"), file);
        }
    }

    [Fact]
    public void BasePresentationUsesUnoReferenceApisWithoutWindowsAppSdk()
    {
        var project = XDocument.Load(Path.Combine(RepositoryRoot(), "Plugins",
            "AniMeido.Plugin.Base.Desktop", "AniMeido.Plugin.Base.Desktop.csproj"));

        Assert.Equal("Uno.Sdk", (string?)project.Root!.Attribute("Sdk"));
        Assert.Equal("net10.0", project.Descendants("TargetFramework").Single().Value);
        Assert.Empty(project.Descendants("UseWinUI"));
        Assert.DoesNotContain(project.Descendants("PackageReference"),
            item => (string?)item.Attribute("Include") == "Microsoft.WindowsAppSDK");
        Assert.Contains(project.Descendants("ProjectReference"),
            item => ((string?)item.Attribute("Include"))?.Contains("AniMeido.Plugin.Base.Core") == true);
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public void DesktopDragVisualUsesTheImplementedBitmapImageApi()
    {
        var root = RepositoryRoot();
        var desktop = File.ReadAllText(Path.Combine(root, "Plugins", "AniMeido.Plugin.Base.Desktop",
            "Services", "AnimeCardDragTokenVisualFactory.cs"));
        Assert.Contains("SetContentFromBitmapImage", desktop);
        Assert.DoesNotContain("BitmapDecoder", desktop);
        Assert.DoesNotContain("SetContentFromSoftwareBitmap", desktop);
    }

    [Theory]
    [InlineData("Views/Controls/AnimeCard.xaml.cs")]
    [InlineData("Views/PastSeasonPage.xaml.cs")]
    [InlineData("Views/GlobalSearchPage.xaml.cs")]
    [InlineData("Views/TagSearchResultPage.xaml.cs")]
    [InlineData("Views/PersonSearchResultPage.xaml.cs")]
    [Trait("Category", "Mechanism")]
    public void NeutralLibraryKeepsDesktopPresentationPartialsInItsCompilation(string file)
    {
        Assert.True(File.Exists(Path.Combine(RepositoryRoot(), "Plugins",
            "AniMeido.Plugin.Base.Desktop", file)));
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public void DesktopLocalMatchesLoadCoversWithoutPhasedContainerCallbacks()
    {
        var page = XDocument.Load(Path.Combine(RepositoryRoot(), "Plugins",
            "AniMeido.Plugin.Base.Desktop", "Views", "GlobalSearchPage.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var cover = page.Descendants().Single(node => (string?)node.Attribute(x + "Name") == "LocalMatchCover");
        Assert.Equal("OnLocalCoverLoaded", (string?)cover.Attribute("Loaded"));
        Assert.Equal("OnLocalCoverChanged", (string?)cover.Attribute("DataContextChanged"));
    }

    private static string RepositoryRoot()
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
