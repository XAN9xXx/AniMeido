using AniMeido.App.Services;
using AniMeido.Plugin.Base.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Xunit;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopElementRoleTests
{
    [Fact]
    public Task ATitleFollowsItsRoleUntilUnloadedAndResumesWhenLoadedAgain()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var title = new TextBlock { Text = "Title" };
            DesktopElementAppearance.SetRole(title, DesktopElementRole.Title);
            root.Children.Add(title);
            root.UpdateLayout();
            await Task.Delay(20);
            Application.Current.Resources["DesktopTitleSize"] = 41d;
            DesktopPresentationPreferences.NotifyAppearanceChanged();
            Assert.Equal(41, title.FontSize);
            root.Children.Remove(title);
            await Task.Delay(20);
            Application.Current.Resources["DesktopTitleSize"] = 37d;
            DesktopPresentationPreferences.NotifyAppearanceChanged();
            Assert.Equal(41, title.FontSize);
            root.Children.Add(title);
            root.UpdateLayout();
            await Task.Delay(20);
            Assert.Equal(37, title.FontSize);
        });

    [Fact]
    public Task ExplicitSizesStayFixedWhileTheirFontFamilyChanges()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var text = new TextBlock { Text = "Sized", FontSize = 19 };
            DesktopElementAppearance.SetRole(text, DesktopElementRole.Control);
            root.Children.Add(text);
            root.UpdateLayout();
            await Task.Delay(20);
            Application.Current.Resources["DesktopUiFontFamily"] = new FontFamily("Segoe UI");
            Application.Current.Resources["DesktopControlTextSize"] = 22d;
            DesktopPresentationPreferences.NotifyAppearanceChanged();
            Assert.Equal(19, text.FontSize);
            Assert.Equal("Segoe UI", text.FontFamily.Source);
        });

    [Fact]
    public Task ContainerRolesLeaveTheContentPropertiesAlone()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var child = new TextBlock { Text = "Child", FontSize = 23 };
            var border = new Border { Child = child };
            DesktopElementAppearance.SetRole(border, DesktopElementRole.SectionSurface);
            root.Children.Add(border);
            root.UpdateLayout();
            await Task.Delay(20);
            Application.Current.Resources["DesktopSectionRadius"] = new CornerRadius(9);
            Application.Current.Resources["DesktopControlTextSize"] = 14d;
            DesktopPresentationPreferences.NotifyAppearanceChanged();
            Assert.Equal(new CornerRadius(9), border.CornerRadius);
            Assert.Equal(23, child.FontSize);
        });
}
