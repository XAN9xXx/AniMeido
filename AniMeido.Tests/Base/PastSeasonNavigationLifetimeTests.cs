using System.Text.RegularExpressions;

namespace AniMeido.Tests;

[Trait("Category", "Mechanism")]
public sealed class PastSeasonNavigationLifetimeTests
{
    [Fact]
    public void LoadBoundaryOwnsItsCancellationSourceUntilTheActualRequestFinishes()
    {
        var body = PageMethod("private async Task LoadSeasonSafelyAsync(", "private void OnRefreshClick(");
        var source = Regex.Match(body, @"using\s+var\s+(\w+)\s*=\s*new\s+CancellationTokenSource\s*\(");
        Assert.True(source.Success, "The load boundary must own its source with a local using declaration.");
        var owner = source.Groups[1].Value;
        var token = Regex.Match(body, @"var\s+(\w+)\s*=\s*" + Regex.Escape(owner) + @"\.Token\s*;");
        Assert.True(token.Success, "Capture the operation token before awaiting cache or season work.");
        var tokenName = token.Groups[1].Value;
        Assert.True(token.Index < body.IndexOf("await ", StringComparison.Ordinal));
        Assert.Contains("_loadCts?.Cancel()", body, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"_loadCts\??\.Dispose\s*\(", body);
        Assert.Matches(@"LoadPastSeasonAnimeAsync\([^;]*,\s*" + Regex.Escape(tokenName) + @"\s*\)", body);
        Assert.Matches(@"finally\s*\{\s*if\s*\(\s*ReferenceEquals\(\s*_loadCts\s*,\s*"
            + Regex.Escape(owner) + @"\s*\)\s*\)\s*(?:\{\s*)?_loadCts\s*=\s*null", body);
    }

    [Fact]
    public void UnloadSuspendsViewModelBeforeCancellingAndDoesNotDisposeTheRunningRequestsSource()
    {
        var body = PageMethod("private void OnRootGridUnloaded(", null);
        var suspend = body.IndexOf("ViewModel.Suspend()", StringComparison.Ordinal);
        var cancel = body.IndexOf("_loadCts?.Cancel()", StringComparison.Ordinal);
        var clear = body.IndexOf("_loadCts = null", StringComparison.Ordinal);

        Assert.True(suspend >= 0 && cancel > suspend && clear > cancel);
        Assert.Contains("Interlocked.Increment(ref _loadVersion)", body, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"_loadCts\??\.Dispose\s*\(", body);
    }

    [Fact]
    public void FinalErrorBoundaryFiltersOwnCancellationAndChecksRequestIdentityBeforeReportingError()
    {
        var body = PageMethod("private async Task LoadSeasonSafelyAsync(", "private void OnRefreshClick(");
        var token = Regex.Match(body, @"var\s+(\w+)\s*=\s*\w+\.Token\s*;");
        Assert.True(token.Success, "The final boundary must retain its own operation token.");
        var tokenName = Regex.Escape(token.Groups[1].Value);
        Assert.Matches(@"catch\s*\(OperationCanceledException\)\s*when\s*\(\s*" + tokenName
            + @"\.IsCancellationRequested\s*\)", body);
        var handler = body.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
        Assert.True(handler >= 0, "The load boundary must own unexpected failures.");
        var error = body[handler..];
        var current = Regex.Match(body, @"bool\s+IsCurrentLoad\(\)\s*=>\s*([^;]+);");
        Assert.True(current.Success, "Request identity must use the captured version and token.");
        Assert.Contains("_loadVersion", current.Groups[1].Value, StringComparison.Ordinal);
        Assert.Matches(tokenName + @"\.IsCancellationRequested", current.Groups[1].Value);
        var version = Regex.Match(body, @"var\s+(\w+)\s*=\s*Interlocked\.Increment\(ref\s+_loadVersion\)");
        Assert.True(version.Success);
        Assert.Matches(@"\b" + Regex.Escape(version.Groups[1].Value) + @"\b", current.Groups[1].Value);
        var guard = error.IndexOf("IsCurrentLoad()", StringComparison.Ordinal);
        var log = error.IndexOf("_logger.LogError(", StringComparison.Ordinal);
        var display = error.IndexOf("ErrorInfoBar.Message", StringComparison.Ordinal);
        Assert.True(guard >= 0 && log > guard && display > log);
        Assert.Matches(@"if\s*\(\s*!IsCurrentLoad\(\)\s*\)\s*(?:\{\s*)?return\s*;", error[..log]);
    }

    private static string PageMethod(string start, string? next)
    {
        var path = Path.Combine(AppDataRootsTests.RepositoryRoot, "Plugins", "AniMeido.Plugin.Base",
            "Views", "PastSeasonPage.xaml.cs");
        var source = File.ReadAllText(path);
        var first = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(first >= 0, $"Past-season page method not found: {start}");
        if (next is null)
            return source[first..];

        var last = source.IndexOf(next, first + start.Length, StringComparison.Ordinal);
        Assert.True(last > first, $"Past-season page method not found: {next}");
        return source[first..last];
    }
}
