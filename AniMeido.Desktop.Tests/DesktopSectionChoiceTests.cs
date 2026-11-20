using System.Xml.Linq;
using Xunit;

namespace AniMeido.Desktop.Tests;

public sealed class DesktopSectionChoiceTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    [Trait("Category", "Mechanism")]
    public void FilledSectionChoiceKeepsNativeToggleSemanticsAndUnboundedContent()
    {
        var template = ReadTemplate();
        Assert.Equal("ToggleButton", (string?)template.Attribute("TargetType"));
        var presenter = Assert.Single(template.Descendants(), element => element.Name.LocalName == "ContentPresenter");
        foreach (var property in new[] { "Content", "ContentTemplate", "ContentTemplateSelector", "ContentTransitions",
                     "Padding", "HorizontalContentAlignment", "VerticalContentAlignment" })
            Assert.Equal($"{{TemplateBinding {property}}}", (string?)presenter.Attribute(property));
        Assert.Equal("{ThemeResource DesktopSubtleButtonBackgroundBrush}", (string?)presenter.Attribute("Background"));
        Assert.Equal("{ThemeResource DesktopTextBrush}", (string?)presenter.Attribute("Foreground"));
        Assert.Null(presenter.Attribute("Width"));
        Assert.Null(presenter.Attribute("Height"));
        Assert.Contains(template.Descendants().Attributes(), attribute =>
            attribute.Name.LocalName == "Control.IsTemplateFocusTarget" && attribute.Value == "True");
        Assert.DoesNotContain(template.Descendants(), element => element.Name.LocalName == "Button");
        Assert.DoesNotContain(template.Descendants(), element =>
            (string?)element.Attribute("VerticalAlignment") == "Bottom"
            && (string?)element.Attribute("Background") == "{ThemeResource DesktopAccentBrush}");
    }

    [Theory]
    [Trait("Category", "Mechanism")]
    [InlineData("Checked")]
    [InlineData("CheckedPointerOver")]
    [InlineData("CheckedPressed")]
    [InlineData("CheckedDisabled")]
    public void SelectedSectionUsesTheLightSelectionSurfaceAndKeepsMainText(string state)
    {
        var template = ReadTemplate();
        var presenter = Assert.Single(template.Descendants(), element => element.Name.LocalName == "ContentPresenter");
        var presenterName = Assert.IsType<string>((string?)presenter.Attribute(Xaml + "Name"));
        Assert.Contains(State(template, state).Descendants(), element => element.Name.LocalName == "Setter"
            && (string?)element.Attribute("Target") == presenterName + ".Background"
            && (string?)element.Attribute("Value") == "{ThemeResource DesktopSectionSelectedBrush}");
        Assert.DoesNotContain(State(template, state).Descendants(), element => element.Name.LocalName == "Setter"
            && ((string?)element.Attribute("Target"))?.EndsWith(".Foreground", StringComparison.Ordinal) == true
            && (string?)element.Attribute("Value") != "{ThemeResource DesktopTextBrush}");
    }

    [Theory]
    [Trait("Category", "Mechanism")]
    [InlineData("PointerOver", "DesktopButtonHoverBrush")]
    [InlineData("Pressed", "DesktopButtonPressedBrush")]
    public void UnselectedPointerFeedbackUsesNeutralResources(string state, string brush)
    {
        Assert.Contains(State(ReadTemplate(), state).Descendants(), element => element.Name.LocalName == "Setter"
            && ((string?)element.Attribute("Target"))?.EndsWith(".Background", StringComparison.Ordinal) == true
            && (string?)element.Attribute("Value") == $"{{ThemeResource {brush}}}");
    }

    [Theory]
    [Trait("Category", "Mechanism")]
    [InlineData("Disabled")]
    [InlineData("CheckedDisabled")]
    public void DisabledSectionDimsItsWholePresentation(string state)
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
        var path = Path.Combine(FindRepository(), "AniMeido.Desktop", "Themes", "DesktopSectionChoices.xaml");
        Assert.True(File.Exists(path), "填色分区导航需要独立的浅色选中模板。");
        var template = Assert.Single(XDocument.Load(path).Root!.Elements());
        Assert.Equal("ControlTemplate", template.Name.LocalName);
        Assert.Equal("DesktopFilledSectionTabTemplate", (string?)template.Attribute(Xaml + "Key"));
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
