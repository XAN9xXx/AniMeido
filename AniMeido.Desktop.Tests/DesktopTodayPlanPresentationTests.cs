using System.Xml.Linq;
using Xunit;

namespace AniMeido.Desktop.Tests;

[Trait("Category", "Mechanism")]
public sealed class DesktopTodayPlanPresentationTests
{
    [Fact]
    public void CollapsedPlanRowsHideRearTextWithoutRotatingTheList()
    {
        var page = LoadPage();
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var template = page.Descendants().Single(element =>
            (string?)element.Attribute(xaml + "Key") == "PlanRowCompactTemplate");
        var panel = page.Descendants().Single(element => element.Name.LocalName == "TodayPlanStackPanel");

        Assert.Equal("CardPackFace", (string?)template.Elements().Single().Attribute(xaml + "Name"));
        Assert.Equal("True", (string?)panel.Attribute("HideRearContent"));
        Assert.Equal("0", (string?)panel.Attribute("FanAngleStep"));
    }

    [Fact]
    public void OverflowPlansHaveAnOpeningButtonIndependentOfTheReminderFooter()
    {
        var page = LoadPage();
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var button = Assert.Single(page.Descendants(), element =>
            (string?)element.Attribute(xaml + "Name") == "DesktopPlanMoreButton");

        Assert.Equal("Button", button.Name.LocalName);
        Assert.Contains("PlanStackHint", (string?)button.Attribute("Content") ?? string.Empty);
        Assert.Contains("HasOverflowPlans", (string?)button.Attribute("Visibility") ?? string.Empty);
        Assert.False(string.IsNullOrWhiteSpace((string?)button.Attribute("Click")));
        Assert.DoesNotContain(button.Ancestors(), element =>
            (string?)element.Attribute(xaml + "Name") == "PlanFooter");
    }

    private static XDocument LoadPage()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
            directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AniMeido.slnx")))
                return XDocument.Load(Path.Combine(directory.FullName, "Plugins",
                    "AniMeido.Plugin.Base.Desktop", "Views", "TodayPage.xaml"));
        }
        throw new DirectoryNotFoundException("找不到 AniMeido.slnx。");
    }
}
