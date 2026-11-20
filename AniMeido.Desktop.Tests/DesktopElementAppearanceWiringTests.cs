using System.Xml.Linq;
using Xunit;

namespace AniMeido.Desktop.Tests;

[Trait("Category", "Mechanism")]
public sealed class DesktopElementAppearanceWiringTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("AniMeidoPageTitleStyle")]
    [InlineData("AniMeidoSectionTitleStyle")]
    [InlineData("DesktopSettingHeadingStyle")]
    [InlineData("DesktopControlTextStyle")]
    [InlineData("DesktopEmphasisTextStyle")]
    [InlineData("DesktopDataTextStyle")]
    [InlineData("DesktopEmphasisDataTextStyle")]
    [InlineData("DesktopReadingTextStyle")]
    public void TextRolesApplyCurrentAppearanceWhenTheirTemplateLoads(string key)
    {
        var styles = LoadNamedStyles();
        Assert.True(HasLoadedRefresh(styles[key], styles), key);
    }

    [Theory]
    [InlineData("ComboBox")]
    [InlineData("ComboBoxItem")]
    [InlineData("TextBox")]
    [InlineData("ContentDialog")]
    public void NativeChoicesRefreshLateCreatedControlAndItemTemplates(string target)
    {
        var document = LoadResources();
        var style = Assert.Single(document.Root!.Elements(), element =>
            element.Name.LocalName == "Style"
            && element.Attribute(Xaml + "Key") is null
            && (string?)element.Attribute("TargetType") == target);

        Assert.True(HasLoadedRefresh(style, LoadNamedStyles()), target);
    }

    [Theory]
    [InlineData("AniMeido.Desktop/Views/AppSettingsPage.xaml", "16")]
    public void FixedSizeTitlesInheritLoadedRefreshWithoutLosingTheirSize(string path, string size)
    {
        var page = XDocument.Load(Path.Combine(FindRepository(), path));
        var derived = page.Descendants().Where(element =>
            element.Name.LocalName == "Style"
            && (string?)element.Attribute("BasedOn") == "{StaticResource DesktopEmphasisTextStyle}").ToArray();
        Assert.NotEmpty(derived);
        foreach (var style in derived)
        {
            Assert.True(HasLoadedRefresh(style, LoadNamedStyles()));
            Assert.Contains(style.Elements(), setter =>
                (string?)setter.Attribute("Property") == "FontSize"
                && (string?)setter.Attribute("Value") == size);
        }
    }

    private static bool HasLoadedRefresh(XElement style, IReadOnlyDictionary<string, XElement> styles)
    {
        for (XElement? current = style; current is not null;)
        {
            if (current.Elements().Any(setter =>
                (string?)setter.Attribute("Property") == "services:DesktopElementAppearance.Role"
                && (string?)setter.Attribute("Value") is { } role && role != "None"))
                return true;
            var basedOn = (string?)current.Attribute("BasedOn");
            if (basedOn is null || !basedOn.StartsWith("{StaticResource ", StringComparison.Ordinal))
                return false;
            styles.TryGetValue(basedOn[16..^1], out current);
        }
        return false;
    }

    private static Dictionary<string, XElement> LoadNamedStyles()
        => LoadResources().Root!.Elements()
            .Where(element => element.Name.LocalName == "Style" && element.Attribute(Xaml + "Key") is not null)
            .ToDictionary(element => (string)element.Attribute(Xaml + "Key")!);

    private static XDocument LoadResources()
        => XDocument.Load(Path.Combine(FindRepository(), "AniMeido.Desktop", "Themes", "DesktopThemeResources.xaml"));

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
