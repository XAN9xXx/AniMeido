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
    [InlineData("Views", "AppSettingsPage.Updates.cs",
        "private async void OnRestoreRollbackDataClick(",
        "\n}", "_installedUpdates")]
    public void RestartCallersAwaitTheCompleteRestartOperation(
        string directory, string file, string declaration, string followingDeclaration, string service)
    {
        var body = Between(ReadSource(directory, file), declaration, followingDeclaration);

        Assert.Matches($@"await\s+{Regex.Escape(service)}\.RestartAsync\(", body);
    }

    [Fact]
    public void RestoreReportsRestartFailuresAfterTheRequestHasBeenSaved()
    {
        var source = ReadSource("Views", "AppSettingsPage.Updates.cs");
        var restore = Between(
            source,
            "private async void OnRestoreRollbackDataClick(",
            "\n}");
        var request = restore.IndexOf("await _installedApp.RequestRestoreAsync(", StringComparison.Ordinal);
        var restart = restore.IndexOf("await _installedUpdates.RestartAsync(", StringComparison.Ordinal);
        var report = restore.IndexOf("Serilog.Log.Error(ex,", StringComparison.Ordinal);
        var message = restore.IndexOf("恢复请求已保存，请关闭应用后重新打开", StringComparison.Ordinal);
        Assert.True(request >= 0 && restart > request && report > restart && message > report);
    }

    private static string ReadSource(params string[] relative)
        => File.ReadAllText(Path.Combine(
            new[] { AppDataRootsTests.RepositoryRoot, "AniMeido.Desktop" }
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
