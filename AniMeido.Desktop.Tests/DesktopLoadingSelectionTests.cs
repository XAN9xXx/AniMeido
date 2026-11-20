using AniMeido.App.Services;
using System.Xml.Linq;
using Xunit;

namespace AniMeido.Desktop.Tests;

public sealed class DesktopLoadingSelectionTests
{
    [Theory]
    [InlineData(0, 0, DesktopLoadingAnimation.FiveDots)]
    [InlineData(1, 0, DesktopLoadingAnimation.Arc)]
    [InlineData(2, 0, DesktopLoadingAnimation.ClaudeNodes)]
    [InlineData(2, 1, DesktopLoadingAnimation.ClaudeOrbit)]
    [InlineData(2, 2, DesktopLoadingAnimation.ClaudeBreath)]
    [InlineData(2, 3, DesktopLoadingAnimation.ClaudeQuiet)]
    [InlineData(3, 0, DesktopLoadingAnimation.Gemini)]
    public void AllApprovedFamiliesAndClaudeVariantsAreAvailable(
        int family, int variant, DesktopLoadingAnimation expected)
        => Assert.Equal(expected, DesktopLoadingSelection.Choose(family, variant));

    [Fact]
    public void OneLoadingSessionKeepsItsChoiceUntilLoadingStops()
    {
        var calls = 0;
        var session = new DesktopLoadingSession(() =>
            ++calls == 1 ? DesktopLoadingAnimation.ClaudeOrbit : DesktopLoadingAnimation.Gemini);
        session.Update(false);
        Assert.Equal(0, calls);
        session.Update(true);
        session.Update(true);
        Assert.Equal(DesktopLoadingAnimation.ClaudeOrbit, session.Animation);
        Assert.Equal(1, calls);
        session.Update(false);
        session.Update(true);
        Assert.Equal(DesktopLoadingAnimation.Gemini, session.Animation);
        Assert.Equal(2, calls);
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public void ProgressRingUsesTheOwnedIndicatorWithTheThemeAccent()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AniMeido.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var document = XDocument.Load(Path.Combine(directory.FullName, "AniMeido.Desktop", "Themes", "DesktopThemeResources.xaml"));
        var indicator = Assert.Single(document.Descendants(), element => element.Name.LocalName == "DesktopLoadingIndicator");
        Assert.Equal("{TemplateBinding IsActive}", (string?)indicator.Attribute("IsActive"));
        Assert.Equal("{ThemeResource DesktopAccentBrush}", (string?)indicator.Attribute("Foreground"));
    }
}
