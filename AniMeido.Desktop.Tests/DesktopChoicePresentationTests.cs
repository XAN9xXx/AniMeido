using AniMeido.Plugin.Base.ViewModels;
using System.Xml.Linq;
using Xunit;

namespace AniMeido.Desktop.Tests;

public sealed class DesktopChoicePresentationTests
{
    [Theory]
    [InlineData(false, 2, false)]
    [InlineData(false, 3, false)]
    [InlineData(false, 5, false)]
    [InlineData(true, 0, false)]
    [InlineData(true, 1, false)]
    [InlineData(true, 2, true)]
    [InlineData(true, 3, true)]
    [InlineData(true, 4, false)]
    public void OnlyPaperShortChoicesUseSegments(bool paper, int count, bool expected)
        => Assert.Equal(expected, DesktopChoicePresentation.UseSegments(paper, count));

    [Theory]
    [Trait("Category", "Mechanism")]
    [InlineData("ThemeCombo", 3)]
    [InlineData("VisualStyleChoice", 3)]
    [InlineData("FontStyleChoice", 3)]
    [InlineData("CatalogCardsChoice", 2)]
    [InlineData("UpdateChannelCombo", 2)]
    public void SettingsUseTheSameThemeAwareChoice(string name, int count)
    {
        var document = XDocument.Load(Path.Combine(FindRepository(), "AniMeido.Desktop",
            "Views", "AppSettingsPage.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var choice = document.Descendants().Single(element =>
            (string?)element.Attribute(xaml + "Name") == name);

        Assert.Equal("DesktopChoice", choice.Name.LocalName);
        Assert.Equal(count, choice.Elements(xaml + "String").Count());
        Assert.False(string.IsNullOrWhiteSpace((string?)choice.Attribute("SelectionChanged")));
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
