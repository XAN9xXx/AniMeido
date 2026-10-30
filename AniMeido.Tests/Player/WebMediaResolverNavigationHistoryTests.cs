using System.Text.RegularExpressions;

namespace AniMeido.Tests;

[Trait("Category", "Mechanism")]
public sealed class WebMediaResolverNavigationHistoryTests
{
    [Fact]
    public void BothRealReadersOwnAnObservationUntilTheirFinallyBeforeReset()
    {
        var history = HistoryField();
        var readers = new[]
        {
            ResolverMethod("private async Task<string> LoadPageHtmlOnUiAsync(", "private async Task<bool> VerifyPageAccessOnUiAsync("),
            ResolverMethod("private async Task<bool> VerifyPageAccessOnUiAsync(", "private async Task<T> RunOnUiAsync<T>("),
        };
        foreach (var reader in readers)
        {
            var begin = Regex.Match(reader, @"var\s+(\w+)\s*=\s*" + history + @"\.BeginObservation\(");
            Assert.True(begin.Success, "Each real read must own the callback used to finish its navigation wait.");
            var end = Regex.Match(reader, @"finally\s*\{[\s\S]*?" + history
                + @"\.EndObservation\(\s*" + Regex.Escape(begin.Groups[1].Value) + @"\s*\)\s*;");
            Assert.True(end.Success && end.Index > begin.Index);
            Assert.True(reader.LastIndexOf("ResetWebView(", StringComparison.Ordinal) > end.Index + end.Length,
                "End this observation before reset starts the next about:blank navigation.");
            Assert.DoesNotMatch(@"\.NavigationCompleted\s*\+=", reader);
        }
    }

    [Fact]
    public void ControllerCreationInstallsPermanentStartingAndCompletionRoutes()
    {
        var create = ResolverMethod("private async Task CreateWebViewAsync(", "private void Navigate(");
        Assert.Matches(@"\.NavigationStarting\s*\+=\s*\w+\s*;", create);
        Assert.Matches(@"\.NavigationCompleted\s*\+=\s*\w+\s*;", create);
        var history = HistoryField();
        var source = ResolverSource();
        Assert.Matches(history + @"\.RecordStarting\(\s*\w+\.NavigationId\s*\)", source);
        Assert.Matches(history + @"\.Complete\(\s*(\w+)\.NavigationId\s*,\s*\1\.IsSuccess\s*,\s*"
            + @"\1\.WebErrorStatus\.ToString\(\s*\)\s*\)", source);
    }

    [Fact]
    public void SuccessfulControllerCloseUnhooksRoutesAndClearsHistoryAfterNativeClose()
    {
        var create = ResolverMethod("private async Task CreateWebViewAsync(", "private void Navigate(");
        var close = ResolverMethod("private void CloseHiddenController()", "private async Task<string?> ReadCookiesOnUiAsync(");
        var nativeClose = Regex.Match(close, @"\??\.Close\(\s*\)\s*;");
        Assert.True(nativeClose.Success);
        foreach (var eventName in new[] { "NavigationStarting", "NavigationCompleted" })
        {
            var handler = Regex.Match(create, @"\." + eventName + @"\s*\+=\s*(\w+)\s*;");
            Assert.True(handler.Success);
            var unhook = Regex.Match(close, @"\." + eventName + @"\s*-=\s*"
                + Regex.Escape(handler.Groups[1].Value) + @"\s*;");
            Assert.True(unhook.Success && unhook.Index > nativeClose.Index,
                "A failed native Close must retain the routes and ownership needed for retry.");
        }

        var clear = Regex.Match(close, HistoryField() + @"\.Clear\(\s*\)\s*;");
        Assert.True(clear.Success && clear.Index > nativeClose.Index);
    }

    private static string HistoryField()
    {
        var field = Regex.Match(ResolverSource(), @"readonly\s+WebNavigationHistory\s+(\w+)\s*=\s*new\s*\(\s*\)");
        Assert.True(field.Success, "The resolver must retain history across individual read observations.");
        return Regex.Escape(field.Groups[1].Value);
    }

    private static string ResolverMethod(string start, string next)
    {
        var source = ResolverSource();
        var first = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(first >= 0, $"Resolver method not found: {start}");
        var last = source.IndexOf(next, first + start.Length, StringComparison.Ordinal);
        Assert.True(last > first, $"Resolver method not found: {next}");
        return source[first..last];
    }

    private static string ResolverSource()
    {
        var path = Path.Combine(AppDataRootsTests.RepositoryRoot, "Plugins", "AniMeido.Plugin.Player",
            "Sources", "Web", "WebMediaResolver.cs");
        return Regex.Replace(File.ReadAllText(path), @"//[^\r\n]*", string.Empty);
    }
}
