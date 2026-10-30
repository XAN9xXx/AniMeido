using System.Text.RegularExpressions;

namespace AniMeido.Tests;

[Trait("Category", "Mechanism")]
public sealed class SourceVerificationWindowLifetimeTests
{
    [Fact]
    public void AutomaticNavigationChecksOwnADrainRegistrationThroughTheirEntireCallback()
    {
        var source = WindowSource();
        var scope = Regex.Match(source, @"readonly\s+WebNavigationCallbackScope\s+(\w+)\s*=\s*new\s*\(\s*\)");
        Assert.True(scope.Success, "The window must own its automatic navigation callbacks.");
        var scopeName = Regex.Escape(scope.Groups[1].Value);
        var callback = WindowMethod("private async void OnNavigationCompleted(", "private void OnDocumentTitleChanged(");
        var registration = Regex.Match(callback,
            @"using\s+var\s+(\w+)\s*=\s*" + scopeName + @"\.TryEnter\(\s*\)\s*;");
        Assert.True(registration.Success);
        var registrationName = Regex.Escape(registration.Groups[1].Value);
        var rejected = Regex.Match(callback, @"if\s*\(\s*" + registrationName
            + @"\s+is\s+null(?:\s*\|\|[^)]*)?\s*\)\s*(?:\{\s*return\s*;\s*\}|return\s*;)");
        var delay = callback.IndexOf("await Task.Delay(", StringComparison.Ordinal);
        Assert.True(rejected.Success && rejected.Index > registration.Index && delay > rejected.Index,
            "Reject callbacks after closing, but retain accepted callbacks across every await.");
        Assert.DoesNotMatch(@"\b" + registrationName + @"\??\.Dispose\s*\(", callback);
        Assert.DoesNotContain("WaitForDrainAsync", callback, StringComparison.Ordinal);
    }

    [Fact]
    public void AutomaticChecksRecheckWindowAndNavigationIdentityAfterBothAwaits()
    {
        var starting = WindowMethod("private void OnNavigationStarting(", "private async void OnNavigationCompleted(");
        var identity = Regex.Match(starting, @"(\w+)\s*=\s*args\.NavigationId\s*;");
        Assert.True(identity.Success, "Each navigation must replace the current document identity.");
        var callback = WindowMethod("private async void OnNavigationCompleted(", "private void OnDocumentTitleChanged(");
        var captured = Regex.Match(callback, @"var\s+(\w+)\s*=\s*args\.NavigationId\s*;");
        Assert.True(captured.Success);
        var identityName = Regex.Escape(captured.Groups[1].Value);
        var fieldName = Regex.Escape(identity.Groups[1].Value);
        var changedIdentity = @"(?:" + identityName + @"\s*!=\s*" + fieldName
            + @"|" + fieldName + @"\s*!=\s*" + identityName + @")";
        var directGuard = @"(?:_closed\s*\|\|\s*" + changedIdentity
            + @"|" + changedIdentity + @"\s*\|\|\s*_closed)";
        var checks = Regex.Matches(callback,
            @"if\s*\(\s*(?:!IsCurrentNavigation\(\s*" + identityName + @"\s*\)|"
            + directGuard + @")\s*\)\s*(?:\{\s*return\s*;\s*\}|return\s*;)");
        var delay = callback.IndexOf("await Task.Delay(", StringComparison.Ordinal);
        var script = callback.IndexOf("await PageStillNeedsInteractionAsync(", StringComparison.Ordinal);
        var publish = callback.IndexOf("await CheckAndContinueAsync()", StringComparison.Ordinal);
        Assert.True(delay >= 0 && script > delay && publish > script);
        Assert.Contains(checks.Cast<Match>(), check => check.Index > delay && check.Index < script);
        Assert.Contains(checks.Cast<Match>(), check => check.Index > script && check.Index < publish);
        if (callback.Contains("IsCurrentNavigation(", StringComparison.Ordinal))
        {
            Assert.Matches(@"bool\s+IsCurrentNavigation\(\s*ulong\s+(\w+)\s*\)\s*=>\s*!_closed\s*&&\s*"
                + @"(?:\1\s*==\s*" + fieldName + @"|" + fieldName + @"\s*==\s*\1)\s*;", WindowSource());
        }
    }

    [Fact]
    public void ShowDrainsAutomaticChecksAndVerificationBeforeFinallyReleasingNativeResources()
    {
        var source = WindowSource();
        var scope = Regex.Match(source, @"readonly\s+WebNavigationCallbackScope\s+(\w+)\s*=\s*new\s*\(\s*\)");
        Assert.True(scope.Success);
        var show = WindowMethod("public async Task<bool> ShowAsync(", "public void CancelAndClose(");
        var stop = show.IndexOf("PrepareClose()", StringComparison.Ordinal);
        var cancel = show.IndexOf("_lifetimeCancellation.Cancel()", StringComparison.Ordinal);
        var drain = Regex.Match(show, @"await\s+Task\.WhenAll\(\s*"
            + Regex.Escape(scope.Groups[1].Value) + @"\.WaitForDrainAsync\(\s*\)\s*,\s*"
            + @"_verificationTask\s*\?\?\s*Task\.CompletedTask\s*\)\s*;");
        Assert.True(stop >= 0 && cancel > stop && drain.Success && drain.Index > cancel,
            "ShowAsync cancels only after close preparation, then owns both outstanding task groups.");
        Assert.Matches(@"try\s*\{\s*_lifetimeCancellation\.Cancel\(\s*\)\s*;\s*\}\s*finally\s*\{\s*await\s+Task\.WhenAll", show);
        var finalCleanup = show.IndexOf("finally", drain.Index, StringComparison.Ordinal);
        var nativeClose = show.IndexOf("TryCloseController(", drain.Index, StringComparison.Ordinal);
        var dispose = show.IndexOf("_lifetimeCancellation.Dispose()", StringComparison.Ordinal);
        Assert.True(finalCleanup > drain.Index && nativeClose > finalCleanup && dispose > nativeClose,
            "Even a failed callback must finish before final controller and cancellation-source disposal.");
    }

    [Fact]
    public void NormalAndFailedWindowCloseSharePreparationWithoutReleasingTheControllerEarly()
    {
        var closed = WindowMethod("private void OnClosed(", "private void PrepareClose(");
        var failedClose = WindowMethod("private void CloseWindow()", "private void ResizeWindow()");
        Assert.Contains("PrepareClose()", closed, StringComparison.Ordinal);
        var failure = failedClose.IndexOf("catch", StringComparison.Ordinal);
        Assert.True(failure >= 0 && failedClose.IndexOf("PrepareClose()", failure, StringComparison.Ordinal) > failure);
        Assert.DoesNotContain("TryCloseController(", closed, StringComparison.Ordinal);
        Assert.DoesNotContain("TryCloseController(", failedClose, StringComparison.Ordinal);

        var preparation = WindowMethod("private void PrepareClose(", "private Task CheckAndContinueAsync()");
        var markClosed = preparation.IndexOf("_closed = true", StringComparison.Ordinal);
        var stop = preparation.IndexOf(".StopAccepting()", StringComparison.Ordinal);
        Assert.True(markClosed >= 0 && stop > markClosed);
        Assert.DoesNotContain("_lifetimeCancellation.Cancel()", preparation, StringComparison.Ordinal);
        Assert.Contains("NavigationCompleted -= OnNavigationCompleted", preparation, StringComparison.Ordinal);
        Assert.Contains("_completion.TrySetResult(false)", preparation, StringComparison.Ordinal);
        Assert.DoesNotContain("TryCloseController(", preparation, StringComparison.Ordinal);
        Assert.DoesNotContain("_controller = null", preparation, StringComparison.Ordinal);
        Assert.DoesNotContain("WaitForDrainAsync", preparation, StringComparison.Ordinal);
    }

    [Fact]
    public void VerificationFailureCannotPublishStatusAfterTheWindowCloses()
    {
        var verification = WindowMethod("private async Task CheckAndContinueCoreAsync()", "private static async Task<bool> PageStillNeedsInteractionAsync(");
        var timeout = verification.IndexOf("catch (SourceResolutionException", StringComparison.Ordinal);
        var cancelled = verification.IndexOf("catch (OperationCanceledException", StringComparison.Ordinal);
        var otherFailure = verification.IndexOf("catch (Exception", StringComparison.Ordinal);
        var cleanup = verification.IndexOf("finally", StringComparison.Ordinal);
        Assert.True(timeout >= 0 && cancelled > timeout && otherFailure > cancelled && cleanup > otherFailure);
        foreach (var failure in new[] { verification[timeout..cancelled], verification[otherFailure..cleanup] })
        {
            var guard = Regex.Match(failure, @"if\s*\(\s*!_closed\s*\)|if\s*\(\s*_closed\s*\)\s*return\s*;");
            var publish = failure.IndexOf("_status.Text", StringComparison.Ordinal);
            Assert.True(guard.Success && publish > guard.Index,
                "A verification error may update only a window that is still open.");
        }
    }

    [Fact]
    public void AsyncEventFailuresReachTheAwaitedCompletionAndCloseTheWindow()
    {
        var navigation = WindowMethod("private async void OnNavigationCompleted(", "private void OnDocumentTitleChanged(");
        var click = WindowMethod("private async void OnCompleteClick(", "private void OnCancelClick(");
        foreach (var entry in new[] { navigation, click })
        {
            var failure = Regex.Match(entry, @"catch\s*\(\s*Exception\s+(\w+)\s*\)\s*\{");
            Assert.True(failure.Success, "An async void entry must hand unexpected failures to its awaited owner.");
            var errorName = Regex.Escape(failure.Groups[1].Value);
            var handling = entry[failure.Index..];
            if (Regex.IsMatch(handling, @"ReportCallbackFailure\(\s*" + errorName + @"\s*\)"))
            {
                var report = Regex.Match(WindowSource(),
                    @"void\s+ReportCallbackFailure\(\s*Exception\s+(\w+)\s*\)\s*\{(?<body>[\s\S]*?)\r?\n    \}");
                Assert.True(report.Success);
                errorName = Regex.Escape(report.Groups[1].Value);
                handling = report.Groups["body"].Value;
            }

            var complete = Regex.Match(handling, @"_completion\.TrySetException\(\s*" + errorName + @"\s*\)");
            var close = handling.IndexOf("CloseWindow()", StringComparison.Ordinal);
            Assert.True(complete.Success && close > complete.Index,
                "Publish the original error before close can publish a normal cancellation result.");
        }
    }

    [Fact]
    public void LateCallbackFailureSurvivesCompletedWindowResultAndIsRethrownAfterDrain()
    {
        var report = WindowMethod("private void ReportCallbackFailure(", "private Task CheckAndContinueAsync()");
        var parameter = Regex.Match(report, @"ReportCallbackFailure\(\s*Exception\s+(\w+)\s*\)");
        Assert.True(parameter.Success);
        var saved = Regex.Match(report, @"(\w+)\s*\?\?=\s*" + Regex.Escape(parameter.Groups[1].Value) + @"\s*;");
        var completion = report.IndexOf("_completion.TrySetException(", StringComparison.Ordinal);
        Assert.True(saved.Success && completion > saved.Index,
            "A callback failure needs its own first-error slot even when the window result is already complete.");
        var fieldName = Regex.Escape(saved.Groups[1].Value);
        Assert.Matches(@"Exception\?\s+" + fieldName + @"\s*;", WindowSource());

        var show = WindowMethod("public async Task<bool> ShowAsync(", "public void CancelAndClose(");
        var drain = Regex.Match(show, @"await\s+Task\.WhenAll\([\s\S]*?\)\s*;");
        var rethrow = Regex.Match(show, @"ExceptionDispatchInfo\.Capture\(\s*(\w+)\s*\)\.Throw\(\s*\)\s*;");
        Assert.True(drain.Success && rethrow.Success && rethrow.Index > drain.Index + drain.Length,
            "ShowAsync must observe a late callback failure only after all accepted work has returned.");
        var capturedName = rethrow.Groups[1].Value;
        if (!string.Equals(capturedName, saved.Groups[1].Value, StringComparison.Ordinal))
        {
            Assert.Matches(fieldName + @"\s+is\s*\{\s*\}\s+" + Regex.Escape(capturedName),
                show[(drain.Index + drain.Length)..rethrow.Index]);
        }

        var finalCleanup = show.IndexOf("finally", drain.Index + drain.Length, StringComparison.Ordinal);
        var nativeClose = show.IndexOf("TryCloseController(", StringComparison.Ordinal);
        Assert.True(finalCleanup > rethrow.Index && nativeClose > finalCleanup,
            "Rethrowing the original callback error must still pass through final native-resource disposal.");
    }

    private static string WindowMethod(string start, string next)
    {
        var source = WindowSource();
        var first = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(first >= 0, $"Verification window method not found: {start}");
        var last = source.IndexOf(next, first + start.Length, StringComparison.Ordinal);
        Assert.True(last > first, $"Verification window method not found: {next}");
        return source[first..last];
    }

    private static string WindowSource()
    {
        var path = Path.Combine(AppDataRootsTests.RepositoryRoot, "Plugins", "AniMeido.Plugin.Player",
            "Views", "SourceVerificationWindow.cs");
        return Regex.Replace(File.ReadAllText(path), @"//[^\r\n]*", string.Empty);
    }
}
