using System.Xml.Linq;
using Xunit;

namespace AniMeido.Desktop.Tests;

public sealed class DesktopWindowsSectionNavigationTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    [Trait("Category", "Mechanism")]
    public void WindowsSectionNavigationKeepsTheToggleButtonContentAndNativeFocusTarget()
    {
        var template = ReadTemplate();
        Assert.Equal("ToggleButton", (string?)template.Attribute("TargetType"));
        var presenter = Assert.Single(template.Descendants(), element => element.Name.LocalName == "ContentPresenter");
        foreach (var property in new[] { "Content", "ContentTemplate", "ContentTemplateSelector", "ContentTransitions",
                     "Padding", "HorizontalContentAlignment", "VerticalContentAlignment" })
            Assert.Equal($"{{TemplateBinding {property}}}", (string?)presenter.Attribute(property));
        Assert.Equal("{ThemeResource DesktopTextBrush}", (string?)presenter.Attribute("Foreground"));
        Assert.Contains(template.Descendants().Attributes(), attribute =>
            attribute.Name.LocalName == "Control.IsTemplateFocusTarget" && attribute.Value == "True");
        Assert.DoesNotContain(template.Descendants(), element => element.Name.LocalName == "Button");
        Assert.Null(presenter.Attribute("Width"));
        Assert.Null(presenter.Attribute("Height"));
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public void SelectionUsesAnAccentMarkerAtTheBottomInsteadOfAnAccentFilledSurface()
    {
        var template = ReadTemplate();
        var marker = Assert.Single(template.Descendants(), element =>
            (string?)element.Attribute("Background") == "{ThemeResource DesktopAccentBrush}");
        Assert.Equal("Bottom", (string?)marker.Attribute("VerticalAlignment"));
        Assert.Equal("3", (string?)marker.Attribute("Height"));
        Assert.Equal("0", (string?)marker.Attribute("Opacity"));
        var markerName = Assert.IsType<string>((string?)marker.Attribute(Xaml + "Name"));
        foreach (var state in new[] { "Checked", "CheckedPointerOver", "CheckedPressed", "CheckedDisabled" })
            Assert.Contains(State(template, state).Descendants(), element => element.Name.LocalName == "Setter"
                && (string?)element.Attribute("Target") == markerName + ".Opacity"
                && (string?)element.Attribute("Value") == "1");
        Assert.DoesNotContain(template.Descendants().Where(element => element.Name.LocalName == "Setter"), setter =>
            (string?)setter.Attribute("Value") == "{ThemeResource DesktopAccentBrush}");
    }

    [Theory]
    [Trait("Category", "Mechanism")]
    [InlineData("PointerOver", "DesktopButtonHoverBrush")]
    [InlineData("Pressed", "DesktopButtonPressedBrush")]
    [InlineData("CheckedPointerOver", "DesktopButtonHoverBrush")]
    [InlineData("CheckedPressed", "DesktopButtonPressedBrush")]
    public void PointerFeedbackUsesSharedNeutralSurfaceResources(string state, string brush)
    {
        Assert.Contains(State(ReadTemplate(), state).Descendants(), element => element.Name.LocalName == "Setter"
            && ((string?)element.Attribute("Target"))?.EndsWith(".Background", StringComparison.Ordinal) == true
            && (string?)element.Attribute("Value") == $"{{ThemeResource {brush}}}");
    }

    [Theory]
    [Trait("Category", "Mechanism")]
    [InlineData("Disabled")]
    [InlineData("CheckedDisabled")]
    public void DisabledNavigationDimsBothContentAndSelection(string state)
    {
        var template = ReadTemplate();
        var rootName = Assert.IsType<string>((string?)template.Elements().Single().Attribute(Xaml + "Name"));
        Assert.Contains(State(template, state).Descendants(), element => element.Name.LocalName == "Setter"
            && (string?)element.Attribute("Target") == rootName + ".Opacity"
            && double.TryParse((string?)element.Attribute("Value"), System.Globalization.CultureInfo.InvariantCulture,
                out var opacity) && opacity > 0 && opacity < 1);
    }

    private static XElement State(XElement template, string name)
        => Assert.Single(template.Descendants(), element => element.Name.LocalName == "VisualState"
            && (string?)element.Attribute(Xaml + "Name") == name);

    private static XElement ReadTemplate()
    {
        var file = Path.Combine(FindRepository(), "AniMeido.Desktop", "Themes", "DesktopWindowsSectionNavigation.xaml");
        Assert.True(File.Exists(file), "Windows 分区导航需要独立的下沿标记模板。");
        var dictionary = XDocument.Load(file).Root!;
        var template = Assert.Single(dictionary.Elements());
        Assert.Equal("ControlTemplate", template.Name.LocalName);
        Assert.Equal("DesktopWindowsSectionTabTemplate", (string?)template.Attribute(Xaml + "Key"));
        return template;
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AniMeido.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("找不到 AniMeido.slnx。");
    }
}
