using System.Text.RegularExpressions;

namespace AniMeido.Tests;

[Trait("Category", "Mechanism")]
public sealed class InstalledUpdateFailureWiringTests
{
    [Fact]
    public void ManualInstalledCheckReportsTheOriginalFailureAsAnError()
    {
        var source = ReadSource("Services", "UpdatePromptDialog.cs");
        var installed = Between(
            source,
            "public static async Task ShowAsync(\n        InstalledUpdateResult result,",
            "private static Task ResumeSkippedAsync(");
        var reporting = Between(installed, "var decision = UpdatePromptPolicy.Decide(",
            "if (!decision.ShouldPrompt)");

        Assert.Matches(
            @"if\s*\(\s*trigger\s*==\s*UpdatePromptTrigger\.Manual\s*&&\s*decision\.LogWarning\s*\)\s*(?:\{\s*)?Log\.Error\(\s*result\.Failure\s*,",
            reporting);
        Assert.DoesNotContain("Log.Warning(", reporting, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupInstalledCheckReportsTheOriginalFailureAsAWarning()
    {
        var source = ReadSource("Services", "UpdateStartupTask.cs");
        var installed = source[source.IndexOf(
            "private static async Task CheckInstalledAsync(", StringComparison.Ordinal)..];
        var reporting = Between(installed, "if (decision.LogWarning)",
            "if (!decision.ShouldPrompt)");

        Assert.Matches(@"Log\.Warning\(\s*result\.Failure\s*,", reporting);
        Assert.DoesNotContain("Log.Error(", reporting, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Services", "UpdatePromptDialog.cs",
        "public static async Task ShowAsync(\n        InstalledUpdateResult result,",
        "private static Task ResumeSkippedAsync(", "service")]
    [InlineData("Views", "AppSettingsPage.xaml.cs",
        "private async void OnRestoreRollbackDataClick(",
        "private async void OnDeleteRollbackDataClick(", "_installedUpdates")]
    public void RestartCallersAwaitTheCompleteRestartOperation(
        string directory, string file, string declaration, string followingDeclaration, string service)
    {
        var body = Between(ReadSource(directory, file), declaration, followingDeclaration);

        Assert.Matches($@"await\s+{Regex.Escape(service)}\.RestartAsync\(", body);
    }

    [Fact]
    public void RestoreReportsRestartFailuresAfterTheRequestHasBeenSaved()
    {
        var source = ReadSource("Views", "AppSettingsPage.xaml.cs");
        var restore = Between(
            source,
            "private async void OnRestoreRollbackDataClick(",
            "private async void OnDeleteRollbackDataClick(");
        var request = restore.IndexOf("await _installedApp.RequestRestoreAsync(", StringComparison.Ordinal);
        var committed = restore.IndexOf("restoreRequested = true;", StringComparison.Ordinal);
        var restart = restore.IndexOf("await _installedUpdates.RestartAsync(", StringComparison.Ordinal);

        Assert.Contains("var restoreRequested = false;", restore, StringComparison.Ordinal);
        Assert.True(request >= 0 && committed > request && restart > committed);
        Assert.Matches(
            @"if\s*\(\s*restoreRequested\s*\|\|\s*ex\s+is\s+not\s+(?:AniMeido\.Updates\.Install\.)?InstallException\s*\)\s*(?:\{\s*)?Serilog\.Log\.Error\(\s*ex\s*,",
            restore);
    }

    private static string ReadSource(params string[] relative)
        => File.ReadAllText(Path.Combine(
            new[] { AppDataRootsTests.RepositoryRoot, "AniMeido.App" }
                .Concat(relative).ToArray())).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Between(string source, string declaration, string followingDeclaration)
    {
        var first = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(first >= 0, $"Declaration not found: {declaration}");
        var last = source.IndexOf(followingDeclaration, first + declaration.Length, StringComparison.Ordinal);
        Assert.True(last > first, $"Following declaration not found: {followingDeclaration}");
        return source[first..last];
    }
}
