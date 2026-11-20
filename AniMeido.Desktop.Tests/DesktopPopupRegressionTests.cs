using AniMeido.App.Services;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using AniMeido.Plugin.Base.Views;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System.Reflection;
using Xunit;
using static AniMeido.Desktop.Tests.DesktopTodayRecommendationPageTests;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopPopupRegressionTests
{
    [Theory]
    [InlineData(DesktopVisualStyle.Fluent10)]
    [InlineData(DesktopVisualStyle.Fluent11)]
    [InlineData(DesktopVisualStyle.Paper)]
    public Task RecommendationPreferencesAreOrdinaryRowsWithoutDeckOrPopup(DesktopVisualStyle visual)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var store = new DesktopSettingsStore(fixture);
            await store.SaveAsync(new DesktopSettings { VisualStyle = visual.ToString(), ThemeSetting = "Dark" });
            await using var theme = new ThemeService(store);
            await theme.InitializeThemeAsync(root);
            var page = fixture.Recommendation(new Source
            {
                Tags = (_, _) => Task.FromResult(new List<Tag> { new("科幻"), new("冒险"), new("治愈") }),
            }, State());
            try
            {
                await AttachAsync(root, page);
                await page.PendingActivation;
                ((Expander)page.FindName("TagPreferencesExpander")).IsExpanded = true;
                root.UpdateLayout();
                await Task.Delay(20);
                var list = (ItemsControl)page.FindName("TagPackList");
                Assert.IsType<StackPanel>(list.ItemsPanelRoot);
                Assert.Null(page.FindName("TagStackPopup"));
                foreach (var name in new[] { "科幻", "冒险", "治愈" })
                {
                    var choice = Find<DesktopChoice>(page, control => control.Tag is RecommendationTagPreference tag && tag.Name == name);
                    var editor = Find<Control>(choice, control => control is ComboBox or ListBox && control.Visibility == Visibility.Visible);
                    Assert.True(editor.IsTabStop);
                    Assert.True(choice.ActualHeight > 0);
                    Assert.True(choice.IsHitTestVisible);
                    Assert.Equal(1, choice.SelectedIndex);
                }
            }
            finally { await DetachAsync(root, page); await page.PendingTags; }
        });

    [Theory]
    [InlineData(DesktopVisualStyle.Fluent10)]
    [InlineData(DesktopVisualStyle.Fluent11)]
    [InlineData(DesktopVisualStyle.Paper)]
    [Trait("Category", "Mechanism")]
    public Task PlanPopupIgnoresUnchangedRootNotificationsAndRepeatedCloseRestoresOnce(DesktopVisualStyle visual)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var store = new DesktopSettingsStore(fixture);
            await store.SaveAsync(new DesktopSettings { VisualStyle = visual.ToString(), ThemeSetting = "Dark" });
            await using var theme = new ThemeService(store);
            await theme.InitializeThemeAsync(root);
            var action = new ActionCenterService(fixture.Factory);
            foreach (var id in Enumerable.Range(1, 5)) await action.UpsertPlanAsync(id, $"计划{id}", AnimePlanPriority.Normal, null, id);
            var page = fixture.Today(new Source());
            try
            {
                await AttachAsync(root, page);
                await page.PendingActivation;
                root.UpdateLayout();
                var overlay = (DesktopDeckOverlay)typeof(TodayPage).GetField("_plans", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
                var host = (Grid)page.FindName("PlanCard");
                var surface = (Grid)page.FindName("PlanVisual");
                var closed = 0;
                overlay.Closed += (_, _) => closed++;
                for (var iteration = 0; iteration < 12; iteration++)
                {
                    await page.OpenPlansAsync();
                    Assert.True(page.IsPlanDeckOpen);
                    typeof(DesktopDeckOverlay).GetMethod("OnRootChanged", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .Invoke(overlay, [root.XamlRoot, null]);
                    Assert.True(page.IsPlanDeckOpen);
                    if (iteration % 2 == 0) overlay.Close();
                    else ((Popup)page.FindName("PlanStackPopup")).IsOpen = false;
                    overlay.Close();
                    root.UpdateLayout();
                    await Task.Delay(10);
                    Assert.False(page.IsPlanDeckOpen);
                    Assert.Same(surface, Assert.Single(host.Children));
                    Assert.Empty(((Canvas)page.FindName("PlanDismissCanvas")).Children);
                    Assert.Equal(iteration + 1, closed);
                    Assert.False(((TodayPlanStackPanel)((ItemsControl)page.FindName("VisiblePlanList")).ItemsPanelRoot).Expanded);
                }
                // Close 的原地恢复与框架延后发送的 Closed 之间重新打开。
                await page.OpenPlansAsync();
                overlay.Close();
                root.UpdateLayout();
                await page.OpenPlansAsync();
                Assert.True(page.IsPlanDeckOpen);
                await Task.Delay(20);
                Assert.True(page.IsPlanDeckOpen);
                Assert.DoesNotContain(surface, host.Children);
                Assert.Same(surface, Assert.Single(((Canvas)page.FindName("PlanDismissCanvas")).Children));
                overlay.Close();
            }
            finally { await DetachAsync(root, page); await page.PendingActivation; await page.ViewModel.Theme.PendingLoad; }
        });
}
