using System.Xml.Linq;
using Xunit;

namespace AniMeido.Desktop.Tests;

[Trait("Category", "Mechanism")]
public sealed class DesktopDialogVisualRulesTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void DialogPresentationUsesTheOwnedTemplateAndButtonBehavior()
    {
        var style = DialogStyle();

        Assert.Null(style.Attribute("BasedOn"));
        Assert.Contains(style.Elements(), setter => (string?)setter.Attribute("Property") == "Template"
            && (string?)setter.Attribute("Value") == "{StaticResource DesktopContentDialogTemplate}");
        Assert.DoesNotContain(style.Elements(), setter => (string?)setter.Attribute("Property")
            is "PrimaryButtonStyle" or "SecondaryButtonStyle" or "CloseButtonStyle");
    }

    [Theory]
    [InlineData("CornerRadius", "DesktopDialogRadius")]
    [InlineData("BorderThickness", "DesktopDialogBorderThickness")]
    [InlineData("BorderBrush", "DesktopDialogEdgeBrush")]
    [InlineData("Background", "DesktopDialogFooterBrush")]
    [InlineData("Foreground", "DesktopTextBrush")]
    public void DialogBindsItsOwnAppearanceResources(string property, string resource)
        => Assert.Contains(DialogStyle().Elements(), setter =>
            (string?)setter.Attribute("Property") == property
            && (string?)setter.Attribute("Value") == "{ThemeResource " + resource + "}");

    [Theory]
    [InlineData("DesktopClassicDialogRadius", "0")]
    [InlineData("DesktopModernDialogRadius", "8")]
    [InlineData("DesktopPaperDialogRadius", "6")]
    [InlineData("DesktopClassicDialogBorderThickness", "2")]
    [InlineData("DesktopStandardDialogBorderThickness", "1")]
    public void DialogGeometryMatchesTheApprovedThemeValues(string key, string expected)
        => Assert.Equal(expected, Resource(key).Value);

    private static XElement DialogStyle()
        => Assert.Single(LoadResources().Root!.Elements(), element =>
            element.Name.LocalName == "Style"
            && element.Attribute(Xaml + "Key") is null
            && (string?)element.Attribute("TargetType") == "ContentDialog");

    private static XElement Resource(string key)
        => Assert.Single(LoadResources().Root!.Elements(), element =>
            (string?)element.Attribute(Xaml + "Key") == key);

    private static XDocument LoadResources()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
            directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AniMeido.slnx")))
                return XDocument.Load(Path.Combine(directory.FullName,
                    "AniMeido.Desktop", "Themes", "DesktopThemeResources.xaml"));
        }
        throw new DirectoryNotFoundException("找不到 AniMeido.slnx。");
    }
}
