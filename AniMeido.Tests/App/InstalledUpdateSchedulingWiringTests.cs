namespace AniMeido.Tests;

[Trait("Category", "Mechanism")]
public sealed class InstalledUpdateSchedulingWiringTests
{
    [Fact]
    public void PrimaryActionBecomesBusyBeforeEitherRestartOrDownloadCanAwait()
    {
        var source = File.ReadAllText(Path.Combine(
            AppDataRootsTests.RepositoryRoot, "AniMeido.App", "Services", "UpdatePromptDialog.cs"));
        var declaration = source.IndexOf("async Task HandlePrimaryAsync()", StringComparison.Ordinal);
        Assert.True(declaration >= 0);
        var action = source[declaration..];
        var busy = action.IndexOf("busy = true;", StringComparison.Ordinal);
        var disabled = action.IndexOf("dialog.IsPrimaryButtonEnabled = false;", StringComparison.Ordinal);
        var restart = action.IndexOf("if (ready)", StringComparison.Ordinal);
        var wait = action.IndexOf("await service.RestartAsync(", StringComparison.Ordinal);

        Assert.True(busy >= 0 && disabled > busy && restart > disabled && wait > restart);
        Assert.Contains("if (!busy)", source[..declaration], StringComparison.Ordinal);
        Assert.Contains("primaryAction = HandlePrimaryAsync();", source[..declaration], StringComparison.Ordinal);
        Assert.Contains("busy = false;", action[wait..], StringComparison.Ordinal);
        Assert.Contains("dialog.IsPrimaryButtonEnabled = true;", action[wait..], StringComparison.Ordinal);
    }
}
