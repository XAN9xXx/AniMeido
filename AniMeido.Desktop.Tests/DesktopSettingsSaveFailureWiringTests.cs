using System.Xml.Linq;
using Xunit;

namespace AniMeido.Desktop.Tests;

[Trait("Category", "Mechanism")]
public sealed class DesktopSettingsSaveFailureWiringTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void SettingsKeepAnExplicitSaveFailureAndARetryAction()
    {
        var page = XDocument.Load(Find("Views/AppSettingsPage.xaml"));
        var failure = Assert.Single(page.Descendants(), element =>
            (string?)element.Attribute(Xaml + "Name") == "DesktopSettingsSaveFailure");
        Assert.Equal("InfoBar", failure.Name.LocalName);
        Assert.Equal("False", (string?)failure.Attribute("IsClosable"));
        Assert.Equal("Error", (string?)failure.Attribute("Severity"));
        Assert.Equal("更改已生效，设置未能保存。", (string?)failure.Attribute("Message"));
        var retry = Assert.Single(failure.Descendants(), element => element.Name.LocalName == "Button");
        Assert.Equal("重试", (string?)retry.Attribute("Content"));
        Assert.Equal("OnRetryDesktopSettingsSave", (string?)retry.Attribute("Click"));
        var theme = Assert.Single(page.Descendants(), element => (string?)element.Attribute(Xaml + "Name") == "ThemeCombo");
        Assert.Equal("OnDesktopThemeSelectionChanged", (string?)theme.Attribute("SelectionChanged"));
    }

    [Fact]
    public void ThePageObservesPendingSaveStateAndUsesTheSameChangePathForTheme()
    {
        var source = File.ReadAllText(Find("Views/AppSettingsPage.Appearance.cs"));
        Assert.Contains("SettingsSaveStateChanged +=", source);
        Assert.Contains("SettingsSaveStateChanged -=", source);
        Assert.Contains("HasSettingsSaveFailure", source);
        Assert.Contains("DesktopVisualStyle.Paper", source);
        Assert.Contains("RetrySettingsSaveAsync()", source);
        Assert.DoesNotContain("ShowFeedback(", source);
        Assert.Contains("ThemeCombo.SelectedIndex =", source);
        var start = source.IndexOf("private async void OnDesktopThemeSelectionChanged", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("private ", start + 1, StringComparison.Ordinal);
        var themeHandler = end < 0 ? source[start..] : source[start..end];
        Assert.Contains("_syncingAppearance", themeHandler);
        Assert.Contains("!IsLoaded", themeHandler);
        Assert.Contains("ChangeAppearanceAsync(", themeHandler);
    }

    [Fact]
    public void ErrorToastsExposeARetryAndCanBeDismissedAfterTheSaveSucceeds()
    {
        var source = File.ReadAllText(Find("Services/DesktopToastPresenter.cs"));
        Assert.Contains("Func<Task<bool>>? retry = null", source);
        Assert.Contains("public void Dismiss(string message, bool error)", source);
        Assert.Contains("Content = \"重试\"", source);
        Assert.Contains("await retry()", source);
        Assert.Contains("retryButton.IsEnabled = false", source);
        Assert.Contains("retryButton.IsEnabled = true", source);
        Assert.Contains("Dismiss(message, error)", source);
    }

    [Fact]
    public void ChangingVisualStyleRefreshesTheExistingSaveFailurePresentation()
    {
        var source = File.ReadAllText(Find("Services/ThemeService.cs"));
        var start = source.IndexOf("private void OnAppearanceChanged", StringComparison.Ordinal);
        var end = source.IndexOf("private ", start + 1, StringComparison.Ordinal);
        var handler = source[start..end];
        Assert.Contains("SetSettingsSaveFailure(_settingsSaveFailed)", handler);
        Assert.Contains("if (!failed || CurrentAppearance.VisualStyle != DesktopVisualStyle.Paper)", source);
    }

    [Theory]
    [InlineData("OnDesktopThemeSelectionChanged")]
    [InlineData("OnVisualStyleChanged")]
    [InlineData("OnFontStyleChanged")]
    [InlineData("OnCatalogCardsChanged")]
    public void ChoicesCaptureTheSelectionThenLetThePopupCloseBeforeApplying(string method)
    {
        var source = File.ReadAllText(Find("Views/AppSettingsPage.Appearance.cs"));
        var start = source.IndexOf("private async void " + method, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("private ", start + 1, StringComparison.Ordinal);
        var handler = end < 0 ? source[start..] : source[start..end];
        var deferred = handler.IndexOf("await Task.Yield()", StringComparison.Ordinal);
        Assert.True(deferred > handler.IndexOf("var ", StringComparison.Ordinal));
        Assert.Contains("!IsLoaded", handler[(deferred + 1)..]);
        Assert.Contains("ChangeAppearanceAsync(", handler[(deferred + 1)..]);
    }

    private static string Find(string relative)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AniMeido.slnx")))
                return Path.Combine(directory.FullName, "AniMeido.Desktop", relative);
        }
        throw new DirectoryNotFoundException("找不到 AniMeido.slnx。");
    }
}
