namespace AniMeido.Tests;

[Trait("Category", "Mechanism")]
public sealed class PastSeasonPageLoggingTests
{
    [Fact]
    public void StatusReadKeepsItsExistingFinalLoggingBoundaryWithoutThePageReportingItAgain()
    {
        var path = Path.Combine(AppDataRootsTests.RepositoryRoot, "Plugins", "AniMeido.Plugin.Base",
            "Views", "PastSeasonPage.xaml.cs");
        var source = File.ReadAllText(path);
        var start = source.IndexOf("private async Task LoadDragConfigAndStatusesAsync()", StringComparison.Ordinal);
        var end = source.IndexOf("private void OnRootGridUnloaded(", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var body = source[start..end];

        Assert.Contains("await _dragDrop.ReloadConfigAsync();", body, StringComparison.Ordinal);
        Assert.Contains("_logger.LogWarning(ex,", body, StringComparison.Ordinal);
        Assert.Matches(@"await ViewModel\.ReloadStatusesAsync\(\)\s*;", body);
        Assert.DoesNotContain("Library tracking statuses could not be read", body, StringComparison.Ordinal);
    }
}
