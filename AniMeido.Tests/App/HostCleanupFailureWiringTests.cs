namespace AniMeido.Tests;

[Trait("Category", "Mechanism")]
public sealed class HostCleanupFailureWiringTests
{
    [Theory]
    [InlineData("Plugin cleanup failed")]
    [InlineData("Plugin cleanup dispatch failed")]
    public void UnrecoveredCleanupFailureIsAnErrorAndTheHostStillExits(string message)
    {
        var source = File.ReadAllText(Path.Combine(
            AppDataRootsTests.RepositoryRoot, "AniMeido.PluginHost", "App.xaml.cs"));
        var report = source.IndexOf($"HostLog.Error(\"{message}\", ex);", StringComparison.Ordinal);

        Assert.True(report >= 0, "The final cleanup boundary must retain the original exception as an error.");
        var exit = source.IndexOf("Exit();", report, StringComparison.Ordinal);
        Assert.True(exit > report);
        Assert.DoesNotContain($"HostLog.Warning(\"{message}\", ex);", source, StringComparison.Ordinal);
    }
}
