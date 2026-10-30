using System.Text.RegularExpressions;

namespace AniMeido.Tests;

[Trait("Category", "Mechanism")]
public sealed class ManagementBatchDialogTests
{
    [Fact]
    public void ConfirmationAndRemovalUseTheSameSelectionCapturedBeforeShowingTheDialog()
    {
        var body = PageMethod("private async void OnBatchRemoveClick(", "private Task ChangeSelectedAsync(");
        var snapshot = Regex.Match(body, @"var\s+(\w+)\s*=\s*MineList\.SelectedItems\.OfType<MineEntry>\(\s*\)\.ToList\(\s*\)\s*;");
        Assert.True(snapshot.Success, "Confirm the actual selected entries, not only their current count.");
        var entries = Regex.Escape(snapshot.Groups[1].Value);
        var count = Regex.Match(body, @"var\s+(\w+)\s*=\s*" + entries + @"\.Count\s*;");
        var showing = body.IndexOf("await dialog.ShowAsync()", StringComparison.Ordinal);
        var apply = Regex.Match(body, @"await\s+ChangeAsync\(\s*" + entries + @"\s*,\s*null\s*\)");
        Assert.True(count.Success && count.Index > snapshot.Index && showing > count.Index);
        Assert.Contains("{" + count.Groups[1].Value + "}", body[..showing], StringComparison.Ordinal);
        Assert.True(apply.Success && apply.Index > showing);
        Assert.DoesNotContain("MineList.SelectedItems", body[showing..], StringComparison.Ordinal);
        Assert.DoesNotContain("ChangeSelectedAsync(", body[showing..], StringComparison.Ordinal);
    }

    [Fact]
    public void LeavingTheVisitHidesItsDialogAndInvalidatesItsConfirmationBeforeCancellingReads()
    {
        var loaded = PageMethod("private async void OnPageLoaded(", "private void OnPageUnloaded(");
        var unloaded = PageMethod("private void OnPageUnloaded(", "private Task LoadSafelyAsync()");
        const string increment = @"(?:\+\+_visitVersion|_visitVersion\+\+|Interlocked\.Increment\(\s*ref\s+_visitVersion\s*\))";
        var loadedVisit = Regex.Match(loaded, increment);
        var unloadedVisit = Regex.Match(unloaded, increment);
        Assert.True(loadedVisit.Success && loaded.IndexOf("LoadSafelyAsync()", StringComparison.Ordinal) > loadedVisit.Index);
        var capturedDialog = Regex.Match(unloaded, @"var\s+(\w+)\s*=\s*_batchRemoveDialog\s*;");
        var clear = unloaded.IndexOf("_batchRemoveDialog = null", StringComparison.Ordinal);
        Assert.True(unloadedVisit.Success && capturedDialog.Success && capturedDialog.Index > unloadedVisit.Index
            && clear > capturedDialog.Index);
        var hide = Regex.Match(unloaded, @"\b" + Regex.Escape(capturedDialog.Groups[1].Value) + @"\??\.Hide\(\s*\)");
        Assert.True(hide.Success && hide.Index > clear);
        Assert.Matches(@"finally\s*\{\s*ViewModel\.CancelPendingLoads\(\s*\)\s*;\s*\}", unloaded);

        var confirmation = PageMethod("private async void OnBatchRemoveClick(", "private Task ChangeSelectedAsync(");
        var visit = Regex.Match(confirmation, @"(?:var|int)\s+(\w+)\s*=\s*_visitVersion\s*;");
        Assert.True(visit.Success);
        var showing = confirmation.IndexOf("await dialog.ShowAsync()", StringComparison.Ordinal);
        var apply = confirmation.IndexOf("await ChangeAsync(", StringComparison.Ordinal);
        Assert.True(showing > visit.Index && apply > showing);
        var guard = confirmation[showing..apply];
        Assert.Contains("IsLoaded", guard, StringComparison.Ordinal);
        Assert.Matches(@"(?:" + Regex.Escape(visit.Groups[1].Value) + @"\s*(?:==|!=)\s*_visitVersion|"
            + @"_visitVersion\s*(?:==|!=)\s*" + Regex.Escape(visit.Groups[1].Value) + @")", guard);
    }

    [Fact]
    public void DialogAwaitUsesTheErrorBoundaryAndOldVisitFailuresCannotPublishToTheNextVisit()
    {
        var confirmation = PageMethod("private async void OnBatchRemoveClick(", "private Task ChangeSelectedAsync(");
        var boundary = confirmation.IndexOf("RunSafelyAsync(", StringComparison.Ordinal);
        var showing = confirmation.IndexOf("await dialog.ShowAsync()", StringComparison.Ordinal);
        Assert.True(boundary >= 0 && showing > boundary);
        var cleanup = confirmation.IndexOf("finally", showing, StringComparison.Ordinal);
        var ownDialog = Regex.Match(confirmation, @"ReferenceEquals\(\s*(?:_batchRemoveDialog\s*,\s*dialog|dialog\s*,\s*_batchRemoveDialog)\s*\)");
        var clear = confirmation.IndexOf("_batchRemoveDialog = null", showing, StringComparison.Ordinal);
        Assert.True(cleanup > showing && ownDialog.Success && ownDialog.Index > cleanup && clear > ownDialog.Index,
            "Only the finishing dialog may clear its field; a later visit may already own another dialog.");

        var errorBoundary = PageMethod("private async Task RunSafelyAsync(", "private void OnViewModelPropertyChanged(");
        var visit = Regex.Match(errorBoundary, @"(?:var|int)\s+(\w+)\s*=\s*_visitVersion\s*;");
        var operation = errorBoundary.IndexOf("await action()", StringComparison.Ordinal);
        var log = errorBoundary.IndexOf("_logger.LogError(", StringComparison.Ordinal);
        var publish = errorBoundary.IndexOf("ViewModel.ErrorMessage =", StringComparison.Ordinal);
        Assert.True(visit.Success && operation > visit.Index && log > operation && publish > log);
        Assert.Matches(@"if\s*\(\s*IsLoaded\s*&&\s*(?:" + Regex.Escape(visit.Groups[1].Value)
            + @"\s*==\s*_visitVersion|_visitVersion\s*==\s*" + Regex.Escape(visit.Groups[1].Value) + @")\s*\)",
            errorBoundary[log..publish]);
        Assert.Contains("ViewModel.IsError = true", errorBoundary[publish..], StringComparison.Ordinal);
    }

    [Fact]
    public void LatePartialWriteFailureIsLoggedButOnlyTheOriginalVisibleVisitReceivesTheMessage()
    {
        var body = PageMethod("private async Task ChangeAsync(", "private async void OnTagChipTapped(");
        var visit = Regex.Match(body, @"var\s+(\w+)\s*=\s*_visitVersion\s*;");
        var write = body.IndexOf("await ViewModel.ChangeStatusAsync(", StringComparison.Ordinal);
        var log = body.IndexOf("_logger.LogWarning(", StringComparison.Ordinal);
        var publish = body.IndexOf("ViewModel.ErrorMessage =", StringComparison.Ordinal);
        Assert.True(visit.Success && write > visit.Index && log > write && publish > log);
        Assert.Matches(@"if\s*\(\s*!IsLoaded\s*\|\|\s*" + Regex.Escape(visit.Groups[1].Value)
            + @"\s*!=\s*_visitVersion\s*\)\s*(?:\{\s*)?return\s*;", body[log..publish]);
    }

    private static string PageMethod(string start, string next)
    {
        var path = Path.Combine(AppDataRootsTests.RepositoryRoot, "Plugins", "AniMeido.Plugin.Base",
            "Views", "ManagementPage.xaml.cs");
        var source = Regex.Replace(File.ReadAllText(path), @"//[^\r\n]*", string.Empty);
        var first = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(first >= 0, $"Management page method not found: {start}");
        var last = source.IndexOf(next, first + start.Length, StringComparison.Ordinal);
        Assert.True(last > first, $"Management page method not found: {next}");
        return source[first..last];
    }
}
