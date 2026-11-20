using System.Xml.Linq;
using Xunit;

namespace AniMeido.Desktop.Tests;

[Trait("Category", "Mechanism")]
public sealed class DesktopInteractionWiringTests
{
    [Theory]
    [InlineData("DesktopCardHoverBrush")]
    [InlineData("DesktopCardPressedBrush")]
    [InlineData("DesktopSelectionHoverBrush")]
    [InlineData("DesktopSelectionPressedBrush")]
    public void InteractiveSurfacesHaveSeparateStateResources(string key)
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine(Repository(), "AniMeido.Desktop/Themes/DesktopThemeResources.xaml"));
        Assert.Single(document.Root!.Elements(), element => (string?)element.Attribute(xaml + "Key") == key);
        Assert.Contains($"SetBrush(\"{key}\"", Read("ThemeService.cs"));
    }

    [Fact]
    public void ThemeChangesDoNotTraverseOrRewriteFrameworkTemplates()
    {
        var source = Read("ThemeService.cs");
        Assert.DoesNotContain("VisualTreeHelper", source);
        Assert.DoesNotContain("Storyboard.SetTarget", source);
        Assert.DoesNotContain("FindTemplateGrid", source);
        Assert.Contains("DesktopPresentationPreferences.NotifyAppearanceChanged()", source);
    }

    [Fact]
    public void ExplicitRolesSubscribeOnlyWhileTheirElementsAreLoaded()
    {
        var source = Read("DesktopElementAppearance.cs");
        Assert.Contains("element.Loaded +=", source);
        Assert.Contains("element.Unloaded +=", source);
        Assert.Contains("AppearanceChanged +=", source);
        Assert.Contains("AppearanceChanged -=", source);
        Assert.DoesNotContain("VisualTreeHelper", source);
    }

    [Fact]
    public void ExpanderHeadersUseAnInteractiveSurfaceWhileStaticSettingsRowsRemainSeparate()
    {
        var source = Read("ThemeService.cs");
        Assert.Contains("(\"ExpanderHeaderBackground\", \"DesktopFieldBrush\")", source);
        Assert.DoesNotContain("(\"ExpanderHeaderBackground\", \"DesktopSettingRowBrush\")", source);
        Assert.DoesNotContain("ApplyExpanderHeaderAppearance", source);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(Repository(), "AniMeido.Desktop/Services", file));

    private static string Repository()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
            if (File.Exists(Path.Combine(path.FullName, "AniMeido.slnx")))
                return path.FullName;
        throw new DirectoryNotFoundException("找不到 AniMeido.slnx。");
    }
}
