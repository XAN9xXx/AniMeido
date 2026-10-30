using System.Text.RegularExpressions;

namespace AniMeido.Tests;

// 检查真实页面的标记失败接线，不代替原生页面交错验收。
[Trait("Category", "Mechanism")]
public sealed class CurrentSeasonPageStatusLifetimeTests
{
    [Fact]
    public void PageRetainsItsExistingLoggerWithoutChangingItsConstructorDependencies()
    {
        var source = PageSource();
        Assert.Matches(@"private\s+readonly\s+ILogger(?:<CurrentSeasonPage>)?\s+_logger\s*;", source);
        var constructor = PageMethod("public CurrentSeasonPage(", "private void OnPageLoaded(");
        Assert.Matches(@"ILogger<CurrentSeasonPage>\s+logger\s*\)", constructor);
        Assert.Matches(@"_logger\s*=\s*logger\s*;", constructor);
        Assert.Contains("new CurrentSeasonViewModel(dataSource, trackingService, featureState, logger)",
            constructor, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadedAndUnloadedAdvanceTheVisitBeforeStartingOrCancellingPageWork()
    {
        var loaded = PageMethod("private void OnPageLoaded(", "private void OnRootGridUnloaded(");
        var unloaded = PageMethod("private void OnRootGridUnloaded(", "private async Task ReloadDragConfigAndStatusesAsync(");
        const string increment = @"(?:\+\+_visitVersion|_visitVersion\+\+|Interlocked\.Increment\(\s*ref\s+_visitVersion\s*\))";
        var entered = Regex.Match(loaded, increment);
        var left = Regex.Match(unloaded, increment);
        var reload = loaded.IndexOf("ReloadDragConfigAndStatusesAsync()", StringComparison.Ordinal);
        var resume = loaded.IndexOf("ViewModel.ResumeSupplementaryLoads()", StringComparison.Ordinal);
        var cancelMain = unloaded.IndexOf("ViewModel.LoadSeasonalAnimeCommand.Cancel()", StringComparison.Ordinal);
        var cancelSupplementary = unloaded.IndexOf("ViewModel.CancelSupplementaryLoads()", StringComparison.Ordinal);
        Assert.True(entered.Success && reload > entered.Index && resume > entered.Index);
        Assert.True(left.Success && cancelMain > left.Index && cancelSupplementary > left.Index);
        Assert.Contains("rootGrid.Unloaded += OnRootGridUnloaded", loaded, StringComparison.Ordinal);
        var xaml = File.ReadAllText(Path.Combine(AppDataRootsTests.RepositoryRoot, "Plugins",
            "AniMeido.Plugin.Base", "Views", "CurrentSeasonPage.xaml"));
        Assert.Contains("Loaded=\"OnPageLoaded\"", xaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ToggleTimeMachineStatusSafelyAsync", "private void OnTimeMachineYearClick(", "ToggleTimeMachineStatusAsync")]
    [InlineData("ToggleStatusSafelyAsync", "private void OnPickPointerEntered(", "ToggleStatusAsync")]
    public void SqliteFailureIsLoggedForEveryVisitButOnlyTheOriginalVisibleVisitShowsIt(
        string method, string next, string operation)
    {
        var body = PageMethod("private async Task " + method + "(", next);
        var captured = Regex.Match(body, @"(?:var|int)\s+(\w+)\s*=\s*_visitVersion\s*;");
        var write = Regex.Match(body, @"await\s+ViewModel\." + operation + @"\(\s*animeId\s*,\s*status\s*\)\s*;");
        var boundary = Regex.Match(body, @"catch\s*\(\s*(?:Microsoft\.Data\.Sqlite\.)?SqliteException\s+(\w+)\s*\)\s*\{");
        Assert.True(captured.Success && write.Success && write.Index > captured.Index
            && boundary.Success && boundary.Index > write.Index);

        var failure = body[(boundary.Index + boundary.Length)..];
        var log = Regex.Match(failure, @"^\s*_logger\.LogError\(\s*"
            + Regex.Escape(boundary.Groups[1].Value) + @"\s*,[^;]*\)\s*;");
        Assert.True(log.Success, "Log the original exception before checking whether its visit is still visible.");
        var publish = failure.IndexOf("ErrorInfoBar.Message =", StringComparison.Ordinal);
        Assert.True(publish > log.Index + log.Length);
        var visit = Regex.Escape(captured.Groups[1].Value);
        Assert.Matches(@"if\s*\(\s*!IsLoaded\s*\|\|\s*(?:" + visit
            + @"\s*!=\s*_visitVersion|_visitVersion\s*!=\s*" + visit
            + @")\s*\)\s*(?:\{\s*)?return\s*;", failure[(log.Index + log.Length)..publish]);
        Assert.Contains("ErrorInfoBar.IsOpen = true", failure[publish..], StringComparison.Ordinal);
        Assert.Contains("标记失败：", failure[publish..], StringComparison.Ordinal);
        Assert.Single(Regex.Matches(failure, @"\b_logger\.LogError\("));
    }

    [Theory]
    [InlineData("ToggleTimeMachineStatusSafelyAsync", "private void OnTimeMachineYearClick(", "ToggleTimeMachineStatusAsync")]
    [InlineData("ToggleStatusSafelyAsync", "private void OnPickPointerEntered(", "ToggleStatusAsync")]
    public void ConfirmedStatusWriteStillAwaitsItsOriginalOperationWithoutPageCancellation(
        string method, string next, string operation)
    {
        var body = PageMethod("private async Task " + method + "(", next);
        var write = Regex.Match(body, @"await\s+ViewModel\." + operation + @"\(\s*animeId\s*,\s*status\s*\)\s*;");
        Assert.True(write.Success);
        Assert.Single(Regex.Matches(body, @"\bViewModel\." + operation + @"\("));
        Assert.DoesNotMatch(@"\b(?:return|IsLoaded|CancellationToken|ThrowIfCancellationRequested)\b", body[..write.Index]);
        Assert.DoesNotContain(".Cancel(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Run(", body, StringComparison.Ordinal);
    }

    private static string PageMethod(string start, string next)
    {
        var source = PageSource();
        var first = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(first >= 0, $"Current-season page method not found: {start}");
        var last = source.IndexOf(next, first + start.Length, StringComparison.Ordinal);
        Assert.True(last > first, $"Current-season page method not found: {next}");
        return source[first..last];
    }

    private static string PageSource()
    {
        var path = Path.Combine(AppDataRootsTests.RepositoryRoot, "Plugins", "AniMeido.Plugin.Base",
            "Views", "CurrentSeasonPage.xaml.cs");
        return Regex.Replace(File.ReadAllText(path), @"//[^\r\n]*", string.Empty);
    }
}
