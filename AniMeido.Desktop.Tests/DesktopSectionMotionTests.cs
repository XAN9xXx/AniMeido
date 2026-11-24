using AniMeido.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Xunit;
using static AniMeido.Desktop.Tests.DesktopTodayRecommendationPageTests;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopSectionMotionTests
{
    [Theory]
    [InlineData("Fluent10", true)]
    [InlineData("Fluent11", true)]
    [InlineData("Paper", true)]
    [InlineData("Paper", false)]
    public Task RecommendationMovesOnlyTheBodyAndSettlesOnRepeatOrUnload(string style, bool enabled)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var store = new DesktopSettingsStore(fixture);
            await store.SaveAsync(new DesktopSettings { VisualStyle = style });
            await using var theme = new ThemeService(store, null, () => enabled);
            await theme.InitializeThemeAsync(root);
            var page = fixture.Recommendation(new Source(), State());
            try
            {
                await AttachAsync(root, page);
                await page.PendingActivation;
                var host = (Grid)VisualTreeHelper.GetParent((Grid)page.FindName("ProfilePanel"));
                Assert.Null(host.RenderTransform);
                page.ShowSection("profile");
                if (enabled)
                {
                    var t = Assert.IsType<CompositeTransform>(host.RenderTransform);
                    Assert.True(style switch { "Fluent10" => t.ScaleX < 1 && t.ScaleY == t.ScaleX,
                        "Fluent11" => t.TranslateY > 0, _ => t.TranslateX > 0 && host.Opacity == 1 });
                    page.ShowSection("profile");
                    Assert.Same(t, host.RenderTransform);
                    await UnoVisualEvidence.WaitForAsync(() => host.RenderTransform == null);
                    page.ShowSection("hidden");
                    Assert.NotNull(host.RenderTransform);
                }
                else Assert.Null(host.RenderTransform);
                Assert.Null(((FrameworkElement)page.FindName("ProfileSectionButton")).RenderTransform);
                await DetachAsync(root, page);
                Assert.Null(host.RenderTransform);
                Assert.Equal(1, host.Opacity);
            }
            finally { await DetachAsync(root, page); }
        });
}
