using System.Xml.Linq;
using Xunit;

namespace AniMeido.Desktop.Tests;

[Trait("Category", "Mechanism")]
public sealed class DesktopSkiaControlTests
{
    [Fact]
    public void ControlsUseSupportedDefaultsForSingleSelectionAndImmediateScrolling()
    {
        var document = XDocument.Load(Find("Themes/DesktopThemeResources.xaml"));
        Assert.DoesNotContain(document.Descendants().Attributes(), attribute =>
            attribute.Name.LocalName == "IsDeferredScrollingEnabled");
        var segments = document.Descendants().Single(element =>
            (string?)element.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) == "DesktopSegmentsStyle");
        Assert.DoesNotContain(segments.Elements(), setter => (string?)setter.Attribute("Property") == "SelectionMode");
    }

    [Fact]
    public void PaperSwitchUsesExplicitSupportedTransitionsInBothDirections()
    {
        var document = XDocument.Load(Find("Themes/DesktopThemeResources.xaml"));
        foreach (var state in new[] { "On", "Off" })
            Assert.True(document.Descendants().Any(element => element.Name.LocalName == "VisualTransition"
                && (string?)element.Attribute("To") == state
                && element.Descendants().Any(animation => animation.Name.LocalName == "DoubleAnimation"
                    && (string?)animation.Attribute("Duration") == "0:0:0.15")), $"Missing switch transition: {state}");
        Assert.DoesNotContain(document.Descendants().Attributes(), attribute => attribute.Name.LocalName == "GeneratedDuration");
    }

    [Fact]
    public void ReturningToTheWindowRechecksTheSupportedMotionPreference()
    {
        var service = File.ReadAllText(Find("Services/ThemeService.cs"));
        var window = File.ReadAllText(Find("MainWindow.xaml.cs"));
        Assert.DoesNotContain("AnimationsEnabledChanged", service);
        Assert.Contains("RefreshSystemMotionPreference", window);
    }

    private static string Find(string file)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AniMeido.slnx")))
                return Path.Combine(directory.FullName, "AniMeido.Desktop", file);
        throw new DirectoryNotFoundException("找不到 AniMeido.slnx。");
    }
}
