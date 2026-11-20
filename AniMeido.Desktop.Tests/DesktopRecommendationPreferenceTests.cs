using System.Xml.Linq;
using AniMeido.Plugin.Base.ViewModels;
using Xunit;

namespace AniMeido.Desktop.Tests;

public sealed class DesktopRecommendationPreferenceTests
{
    [Theory]
    [InlineData(true, false, 0)]
    [InlineData(false, false, 1)]
    [InlineData(false, true, 2)]
    public void SavedPreferenceSelectsTheSameOptionAcrossPresentations(bool liked, bool reduced, int expected)
        => Assert.Equal(expected, DesktopRecommendationPreference.SelectionIndex(liked, reduced));

    [Fact]
    [Trait("Category", "Mechanism")]
    public void RecommendationUsesTheSharedChoiceWithCommittedSelectionAndOriginalSaveEntry()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AniMeido.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var page = XDocument.Load(Path.Combine(directory.FullName, "Plugins",
            "AniMeido.Plugin.Base.Desktop", "Views", "RecommendationPage.xaml"));
        var selector = Assert.Single(page.Descendants(), element => element.Name.LocalName == "DesktopChoice");

        Assert.Equal("{x:Bind}", (string?)selector.Attribute("Tag"));
        Assert.Contains("IsLiked", (string?)selector.Attribute("SelectedIndex") ?? string.Empty);
        Assert.Contains("IsReduced", (string?)selector.Attribute("SelectedIndex") ?? string.Empty);
        Assert.Contains("OneWay", (string?)selector.Attribute("SelectedIndex") ?? string.Empty);
        Assert.Equal("OnDesktopTagPreferenceChanged", (string?)selector.Attribute("SelectionChanged"));
        Assert.Equal(new[] { "喜欢", "未设置", "减少" }, selector.Elements().Select(option => option.Value));
    }
}
