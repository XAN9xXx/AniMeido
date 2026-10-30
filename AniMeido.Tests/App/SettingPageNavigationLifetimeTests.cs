using System.Text.RegularExpressions;

namespace AniMeido.Tests;

// 检查真实设置页的订阅、返回同步和本地页面复用接线。
[Trait("Category", "Mechanism")]
public sealed class SettingPageNavigationLifetimeTests
{
    [Fact]
    public void EnteringSubscribesBeforeRebuildingTheCurrentSettingsNavigation()
    {
        var body = PageMethod("OnLoaded");
        Assert.Matches(@"if\s*\(\s*_isLoaded\s*\)\s*\{\s*return\s*;\s*\}", body);
        var entering = body.IndexOf("_isLoaded = true", StringComparison.Ordinal);
        var subscription = body.IndexOf("_contributions.Changed += OnContributionsChanged", StringComparison.Ordinal);
        var rebuild = body.IndexOf("RebuildNavigation()", StringComparison.Ordinal);
        Assert.True(entering >= 0 && subscription > entering && rebuild > subscription,
            "Returning to a cached page must rebuild after subscribing to current contributions.");
        Assert.Single(Regex.Matches(body, @"\bRebuildNavigation\("));
    }

    [Fact]
    public void RebuildingUsesTheSelectedTargetIdAndFallsBackToAppSettings()
    {
        var body = PageMethod("RebuildNavigation");
        var selected = body.IndexOf("var selectedId =", StringComparison.Ordinal);
        var clearing = body.IndexOf("SettingsNavigation.MenuItems.Clear()", StringComparison.Ordinal);
        Assert.True(selected >= 0 && clearing > selected);
        Assert.Matches(@"\?\s*selected\.Id\s*:\s*AppSettingsId", body[selected..clearing]);
        Assert.Contains("_contributions.Settings.GroupBy(", body, StringComparison.Ordinal);
        Assert.Matches(@"string\.Equals\(\s*target\.Id\s*,\s*selectedId\s*,\s*StringComparison.Ordinal\s*\)", body);
        Assert.Contains("?? appItem", body, StringComparison.Ordinal);
        var suppressing = body.IndexOf("_suppressSelectionChanged = true", StringComparison.Ordinal);
        var selection = body.IndexOf("SettingsNavigation.SelectedItem = selectedItem", StringComparison.Ordinal);
        var restoring = body.IndexOf("_suppressSelectionChanged = false", StringComparison.Ordinal);
        var showing = body.IndexOf("ShowTarget((SettingsTarget)selectedItem.Tag)", StringComparison.Ordinal);
        Assert.True(suppressing > clearing && selection > suppressing && restoring > selection && showing > restoring);
    }

    [Fact]
    public void ShowingTheSameLocalTargetKeepsItsExistingPage()
    {
        var body = PageMethod("ShowTarget");
        var publishing = body.IndexOf("_currentTargetId = target.Id", StringComparison.Ordinal);
        var creating = body.IndexOf("_pageFactory.CreatePage(target.PageType)", StringComparison.Ordinal);
        Assert.True(publishing >= 0 && creating > publishing);
        var reuse = body[..publishing];
        Assert.Contains("target.PageType is not null", reuse, StringComparison.Ordinal);
        Assert.Matches(@"string\.Equals\(\s*_currentTargetId\s*,\s*target\.Id\s*,\s*StringComparison.Ordinal\s*\)", reuse);
        Assert.Matches(@"SettingsFrame\.Content\s+is\s+not\s+null\s*\)\s*\{\s*return\s*;\s*\}", reuse);
        Assert.Contains("new HostedPluginSettingsPage(", body[creating..], StringComparison.Ordinal);
    }

    [Fact]
    public void LeavingUnsubscribesAndQueuedContributionChangesCheckWhetherThePageIsLoaded()
    {
        var unloaded = PageMethod("OnUnloaded");
        var leaving = unloaded.IndexOf("_isLoaded = false", StringComparison.Ordinal);
        var unsubscribing = unloaded.IndexOf("_contributions.Changed -= OnContributionsChanged", StringComparison.Ordinal);
        Assert.True(leaving >= 0 && unsubscribing > leaving);
        var callback = PageMethod("OnContributionsChanged");
        var queue = callback.IndexOf("DispatcherQueue.TryEnqueue(", StringComparison.Ordinal);
        var visible = callback.IndexOf("if (_isLoaded)", StringComparison.Ordinal);
        var rebuild = callback.IndexOf("RebuildNavigation()", StringComparison.Ordinal);
        Assert.True(queue >= 0 && visible > queue && rebuild > visible);
        Assert.Matches(@"if\s*\(\s*_isLoaded\s*\)\s*\{\s*RebuildNavigation\(\s*\)\s*;\s*\}", callback);
    }

    private static string PageMethod(string name)
    {
        var path = Path.Combine(AppDataRootsTests.RepositoryRoot, "AniMeido.App", "Views", "SettingPage.xaml.cs");
        var source = Regex.Replace(File.ReadAllText(path), @"//[^\r\n]*", string.Empty);
        var start = Regex.Match(source, @"(?m)^ {4}(?:private|public)\s+[^\r\n{;=]*\b" + Regex.Escape(name) + @"\s*\(");
        Assert.True(start.Success, $"Settings page method not found: {name}");
        var remaining = source[(start.Index + start.Length)..];
        var next = Regex.Match(remaining, @"(?m)^ {4}(?:private|public)\s+");
        return next.Success ? source.Substring(start.Index, start.Length + next.Index) : source[start.Index..];
    }
}
