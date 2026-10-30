namespace AniMeido.Tests;

[Trait("Category", "Mechanism")]
public sealed class RecommendationDialogTokenTests
{
    [Fact]
    public void PageActionRequiresAnActiveVisitAndPassesThatVisitsTokenToTheViewModel()
    {
        var body = Method("private Task RunActionAsync(", null);

        Assert.Contains("Func<CancellationToken, Task> action", body, StringComparison.Ordinal);
        Assert.Contains("_navigationCancellation is not { } navigation", body, StringComparison.Ordinal);
        Assert.Contains("return Task.CompletedTask;", body, StringComparison.Ordinal);
        Assert.Contains("ViewModel.RunActionAsync(action, navigation.Token, serialize)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentToken", body, StringComparison.Ordinal);
    }

    [Fact]
    public void PreferenceDialogKeepsThePassedTokenInsteadOfReadingANewVisitAfterConfirmation()
    {
        var body = Method("private async Task ShowPreferenceDialogAsync(", "private void OnOnboardingTagToggle(");

        Assert.Contains("CancellationToken token", body, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentToken", body, StringComparison.Ordinal);
        var confirmation = body.IndexOf("await dialog.ShowAsync()", StringComparison.Ordinal);
        var cancellation = body.IndexOf("token.IsCancellationRequested", confirmation, StringComparison.Ordinal);
        var save = body.IndexOf("await ViewModel.SetPreferenceAsync(", StringComparison.Ordinal);
        Assert.True(confirmation >= 0 && cancellation > confirmation && save > cancellation);
        Assert.Matches(@"SetPreferenceAsync\(\s*feature,\s*adjustment,\s*token\)", body);
    }

    [Theory]
    [InlineData("OnClearHiddenClick", "private async void OnClearPreferencesClick(", "ClearHiddenAsync")]
    [InlineData("OnClearPreferencesClick", "private ContentDialog CreateConfirmationDialog(", "ClearPreferencesAsync")]
    public void ClearConfirmationAndSaveShareTheSameOperationBoundary(
        string method, string next, string saveMethod)
    {
        var body = Method("private async void " + method + "(", next);

        Assert.DoesNotContain("CurrentToken", body, StringComparison.Ordinal);
        var boundary = body.IndexOf("RunActionAsync(async token =>", StringComparison.Ordinal);
        var confirmation = body.IndexOf("await dialog.ShowAsync()", StringComparison.Ordinal);
        var cancellation = body.IndexOf("!token.IsCancellationRequested", StringComparison.Ordinal);
        var save = body.IndexOf("ViewModel." + saveMethod + "(token)", StringComparison.Ordinal);
        Assert.True(boundary >= 0 && confirmation > boundary && cancellation > confirmation && save > cancellation);
    }

    private static string Method(string start, string? next)
    {
        var path = Path.Combine(AppDataRootsTests.RepositoryRoot, "Plugins", "AniMeido.Plugin.Base",
            "Views", "RecommendationPage.xaml.cs");
        var source = File.ReadAllText(path).ReplaceLineEndings("\r\n");
        var first = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(first >= 0, $"Recommendation page method not found: {start}");
        if (next is null)
            return source[first..];

        var last = source.IndexOf(next, first + start.Length, StringComparison.Ordinal);
        Assert.True(last > first, $"Recommendation page method not found: {next}");
        return source[first..last];
    }
}
