using System.Xml.Linq;
using Xunit;

namespace AniMeido.Desktop.Tests;

[Trait("Category", "Mechanism")]
public sealed class DesktopControlVisualRulesTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void NativeComboBoxFocusUsesTheDesktopFieldSurface()
    {
        var source = File.ReadAllText(Path.Combine(FindRepository(), "AniMeido.Desktop", "Services", "ThemeService.cs"));
        Assert.Contains("(\"ComboBoxBackgroundFocused\", \"DesktopFieldFocusBackgroundBrush\")", source);
    }

    [Fact]
    public void ModernWorkspaceAndNavigationKeepSeparateSurfaceLayers()
    {
        Assert.Equal("#F9F9F9", (string?)Resource("DesktopPageBrush").Attribute("Color"));
        Assert.Equal("#F3F3F3", (string?)Resource("DesktopNavigationBrush").Attribute("Color"));
    }

    [Fact]
    public void TextFieldsUseTheirOwnThemeFocusGeometry()
    {
        var template = Resource("DesktopTextBoxTemplate");
        var focus = Assert.Single(template.Descendants(), element =>
            element.Name.LocalName == "VisualState" && (string?)element.Attribute(Xaml + "Name") == "Focused");

        Assert.Contains(focus.Descendants(), element =>
            (string?)element.Attribute("Target") == "BorderElement.BorderThickness"
            && (string?)element.Attribute("Value") == "{ThemeResource DesktopTextFieldFocusThickness}");
        Assert.Contains(focus.Descendants(), element =>
            (string?)element.Attribute("Target") == "BorderElement.BorderBrush"
            && (string?)element.Attribute("Value") == "{ThemeResource DesktopTextFieldFocusBrush}");
    }

    [Theory]
    [InlineData("HeaderContentPresenter")]
    [InlineData("ContentElement")]
    [InlineData("PlaceholderTextContentPresenter")]
    [InlineData("DeleteButton")]
    [InlineData("DescriptionPresenter")]
    public void TextFieldTemplateRetainsItsEditingParts(string name)
        => Assert.Single(Resource("DesktopTextBoxTemplate").Descendants(),
            element => (string?)element.Attribute(Xaml + "Name") == name);

    [Fact]
    public void PaperMenuSelectionUsesACheckmarkInsteadOfTheWindowsPill()
    {
        var style = Resource("DesktopPaperComboBoxItemStyle");
        Assert.Contains(style.Descendants(), element =>
            (string?)element.Attribute(Xaml + "Name") == "PaperChoiceCheckmark");
        Assert.DoesNotContain(style.Descendants(), element =>
            (string?)element.Attribute(Xaml + "Name") == "Pill");
        foreach (var stateName in new[] { "Selected", "SelectedUnfocused", "SelectedPointerOver", "SelectedPressed" })
        {
            var state = Assert.Single(style.Descendants(), element =>
                element.Name.LocalName == "VisualState" && (string?)element.Attribute(Xaml + "Name") == stateName);
            Assert.Contains(state.Descendants(), element =>
                (string?)element.Attribute("Target") == "PaperChoiceCheckmark.Opacity"
                && (string?)element.Attribute("Value") == "1");
        }
    }

    [Theory]
    [InlineData("DesktopButtonEdgeBrush")]
    [InlineData("DesktopButtonHoverEdgeBrush")]
    [InlineData("DesktopPrimaryButtonEdgeBrush")]
    [InlineData("DesktopPrimaryButtonHoverEdgeBrush")]
    public void RaisedButtonStatesHaveSeparateTopAndBottomEdges(string key)
    {
        var brush = Resource(key);
        Assert.Equal("LinearGradientBrush", brush.Name.LocalName);
        Assert.Equal(2, brush.Descendants().Count(element => element.Name.LocalName == "GradientStop"));
    }

    [Theory]
    [InlineData("DesktopButtonPressedEdgeBrush")]
    [InlineData("DesktopPrimaryButtonPressedEdgeBrush")]
    public void PressedButtonsUseFlatEdges(string key)
        => Assert.Equal("SolidColorBrush", Resource(key).Name.LocalName);

    [Theory]
    [InlineData("DesktopClassicFocusMargin", "-4")]
    [InlineData("DesktopModernFocusMargin", "2")]
    [InlineData("DesktopPaperFocusMargin", "3")]
    public void OrdinaryButtonFocusUsesTheDocumentedThemeOffset(string key, string offset)
        => Assert.Equal(offset, Resource(key).Value);

    private static XElement Resource(string key)
    {
        var document = XDocument.Load(Path.Combine(FindRepository(), "AniMeido.Desktop", "Themes", "DesktopThemeResources.xaml"));
        return Assert.Single(document.Root!.Elements(), element => (string?)element.Attribute(Xaml + "Key") == key);
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AniMeido.slnx")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("找不到 AniMeido.slnx。");
    }
}
