using System.Text.RegularExpressions;

namespace AniMeido.Tests;

// 检查真实页面的访问与确认接线，不代替原生选择器或 ContentDialog 验收。
[Trait("Category", "Mechanism")]
public sealed class DataManagementPageLifetimeTests
{
    [Fact]
    public void LoadedAndUnloadedChangeTheVisitAndLeavingHidesOnlyTheOwnedDialog()
    {
        var constructor = PageMethod("DataManagementSettingsPage");
        Assert.Contains("Loaded += OnPageLoaded", constructor, StringComparison.Ordinal);
        Assert.Contains("Unloaded += OnPageUnloaded", constructor, StringComparison.Ordinal);
        var loaded = PageMethod("OnPageLoaded");
        var unloaded = PageMethod("OnPageUnloaded");
        const string increment = @"(?:\+\+_visitVersion|_visitVersion\+\+|Interlocked\.Increment\(\s*ref\s+_visitVersion\s*\))";
        Assert.Matches(increment, loaded);
        var invalidation = Regex.Match(unloaded, increment);
        var captured = Regex.Match(unloaded, @"var\s+(\w+)\s*=\s*_activeDialog\s*;");
        var ownedDialog = captured.Success ? Regex.Escape(captured.Groups[1].Value) : "_activeDialog";
        var hide = Regex.Match(unloaded, @"\b" + ownedDialog + @"\??\.Hide\(\s*\)");
        Assert.True(invalidation.Success && hide.Success
            && hide.Index > invalidation.Index + invalidation.Length);
        Assert.DoesNotMatch(@"\b_activeDialog\s*=\s*null\s*;", unloaded);

        var current = PageMethod("IsCurrentVisit");
        Assert.Contains("IsLoaded", current, StringComparison.Ordinal);
        Assert.Matches(@"(?:visit\s*==\s*_visitVersion|_visitVersion\s*==\s*visit)", current);
    }

    [Fact]
    public void DialogOwnerChecksTheVisitAndRootAndOnlyClearsItsOwnReference()
    {
        var body = PageMethod("ShowDialogAsync");
        Assert.Matches(@"ContentDialog\s+dialog\s*,\s*int\s+visit", body);
        var showing = body.IndexOf("await dialog.ShowAsync()", StringComparison.Ordinal);
        var owned = Regex.Match(body, @"_activeDialog\s*=\s*dialog\s*;");
        Assert.True(showing >= 0 && owned.Success && owned.Index < showing);
        Assert.Contains("IsCurrentVisit(visit)", body[..owned.Index], StringComparison.Ordinal);
        Assert.Matches(@"if\s*\(\s*_activeDialog\s*(?:is\s+not\s+null|!=\s*null)\s*\)\s*(?:\{\s*)?throw\s+new\s+InvalidOperationException\s*\(",
            body[..owned.Index]);
        Assert.Contains("XamlRoot", body[..showing], StringComparison.Ordinal);
        Assert.Matches(@"if\s*\([^;]*XamlRoot[^;]*\)\s*(?:\{\s*)?return\s+ContentDialogResult.None\s*;",
            body[..owned.Index]);
        Assert.Contains("IsCurrentVisit(visit)", body[showing..], StringComparison.Ordinal);
        var cleanup = body.IndexOf("finally", showing, StringComparison.Ordinal);
        var ownReference = Regex.Match(body,
            @"ReferenceEquals\(\s*(?:_activeDialog\s*,\s*dialog|dialog\s*,\s*_activeDialog)\s*\)");
        var clearing = body.LastIndexOf("_activeDialog = null", StringComparison.Ordinal);
        Assert.True(cleanup > showing && ownReference.Success && ownReference.Index > cleanup
            && clearing > ownReference.Index);
    }

    [Theory]
    [InlineData("OnExportBundleClick", "PickSaveFileAsync", "_transfer.ExportBundleAsync(")]
    [InlineData("OnImportBundleClick", "PickSingleFileAsync", "_transfer.ImportBundleAsync(")]
    [InlineData("OnExportClick", "PickSaveFileAsync", "Windows.Storage.FileIO.WriteTextAsync(")]
    [InlineData("OnImportClick", "PickSingleFileAsync", "GetBasicPropertiesAsync(")]
    public void PickerResultsCannotContinueAnObsoleteVisit(string method, string picker, string continuation)
    {
        var body = PageMethod(method);
        var visit = CapturedVisit(body);
        var waiting = Regex.Match(body, @"await\s+\w+\." + Regex.Escape(picker) + @"\(\s*\)");
        var next = body.IndexOf(continuation, waiting.Success ? waiting.Index + waiting.Length : 0,
            StringComparison.Ordinal);
        Assert.True(waiting.Success && next > waiting.Index);
        AssertReturningGuard(body[(waiting.Index + waiting.Length)..next], visit);
        if (method == "OnExportClick")
        {
            var snapshot = Regex.Match(body, @"await\s+_transfer\.ExportJsonAsync\(\s*\)");
            var pickerCreation = body.IndexOf("new Windows.Storage.Pickers.FileSavePicker(", StringComparison.Ordinal);
            Assert.True(snapshot.Success && pickerCreation > snapshot.Index + snapshot.Length);
            AssertReturningGuard(body[(snapshot.Index + snapshot.Length)..pickerCreation], visit);
        }
    }

    [Fact]
    public void JsonImportChecksTheOriginalVisitAfterPropertiesTextAndBackgroundPreview()
    {
        var body = PageMethod("OnImportClick");
        var visit = CapturedVisit(body);
        var properties = Regex.Match(body, @"await\s+\w+\.GetBasicPropertiesAsync\(\s*\)");
        var read = Regex.Match(body, @"await\s+Windows\.Storage\.FileIO\.ReadTextAsync\([^;]*?\)");
        var preview = Regex.Match(body, @"await\s+_transfer\.PreviewJsonAsync\([^;]*?\)");
        Assert.True(properties.Success && read.Success && preview.Success
            && properties.Index < read.Index && read.Index < preview.Index);
        AssertReturningGuard(body[(properties.Index + properties.Length)..read.Index], visit);
        AssertReturningGuard(body[(read.Index + read.Length)..preview.Index], visit);
        var showing = body.IndexOf("await ShowDialogAsync(", preview.Index + preview.Length,
            StringComparison.Ordinal);
        Assert.True(showing > preview.Index);
        AssertReturningGuard(body[(preview.Index + preview.Length)..showing], visit);
        Assert.DoesNotContain("ExportService.Preview(", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("OnImportBundleClick", "ImportBundleAsync")]
    [InlineData("OnImportClick", "ImportJsonAsync")]
    public void ImportStartsOnlyAfterTheOriginalVisitAcceptsItsConfirmation(string method, string importing)
    {
        var body = PageMethod(method);
        var visit = CapturedVisit(body);
        var confirmation = Regex.Matches(body, @"await\s+ShowDialogAsync\(\s*\w+\s*,\s*"
            + Regex.Escape(visit) + @"\s*\)").Cast<Match>().LastOrDefault();
        Assert.NotNull(confirmation);
        var transfer = body.IndexOf("_transfer." + importing + "(", StringComparison.Ordinal);
        Assert.True(transfer > confirmation.Index + confirmation.Length);
        var accepted = body[(confirmation.Index + confirmation.Length)..transfer];
        Assert.Contains("ContentDialogResult.Primary", accepted, StringComparison.Ordinal);
        AssertConfirmationVisitGuard(accepted, visit);
        Assert.DoesNotMatch(@"await\s+\w+\.ShowAsync\(\s*\)", body);
    }

    [Fact]
    public void ClearingCacheCannotSkipConfirmationWhenThePageHasNoCurrentRoot()
    {
        var body = PageMethod("OnClearCacheClick");
        var visit = CapturedVisit(body);
        var confirmation = Regex.Match(body, @"await\s+ShowDialogAsync\(\s*\w+\s*,\s*"
            + Regex.Escape(visit) + @"\s*\)");
        var clear = body.IndexOf("await _cacheService.ClearAllCacheAsync()", StringComparison.Ordinal);
        Assert.True(confirmation.Success && clear > confirmation.Index + confirmation.Length);
        Assert.Contains("XamlRoot", body[..confirmation.Index], StringComparison.Ordinal);
        var accepted = body[(confirmation.Index + confirmation.Length)..clear];
        Assert.Contains("ContentDialogResult.Primary", accepted, StringComparison.Ordinal);
        AssertConfirmationVisitGuard(accepted, visit);
        Assert.DoesNotMatch(@"await\s+\w+\.ShowAsync\(\s*\)", body);
    }

    [Fact]
    public void AllTransferMessagesCarryTheOriginalVisitThroughTheDialogOwner()
    {
        var message = PageMethod("ShowMessageAsync");
        Assert.Matches(@"int\s+visit", message);
        Assert.Matches(@"await\s+ShowDialogAsync\(\s*\w+\s*,\s*visit\s*\)", message);
        Assert.DoesNotMatch(@"await\s+\w+\.ShowAsync\(\s*\)", message);
        foreach (var method in new[]
        {
            "OnExportBundleClick", "OnImportBundleClick", "OnExportClick", "OnImportClick",
            "OnBackupNowClick", "OnClearCacheClick",
        })
        {
            var body = PageMethod(method);
            var visit = CapturedVisit(body);
            var calls = Regex.Matches(body, @"await\s+ShowMessageAsync\([\s\S]*?\)\s*;");
            Assert.NotEmpty(calls.Cast<Match>());
            foreach (Match call in calls)
                Assert.Matches(@",\s*" + Regex.Escape(visit) + @"\s*\)\s*;\s*$", call.Value);
            Assert.DoesNotMatch(@"await\s+\w+\.ShowAsync\(\s*\)", body);
        }
    }

    [Fact]
    public void CacheStatisticsPublishOnlyToTheirOwnVisitAndLoadingStartsFromLoaded()
    {
        var body = PageMethod("UpdateCacheInfoAsync");
        Assert.Matches(@"int\s+visit", body);
        var read = Regex.Match(body, @"await\s+_cacheService\.GetCacheStatsAsync\(\s*\)");
        var publish = body.IndexOf("CacheInfoText.Text =", StringComparison.Ordinal);
        Assert.True(read.Success && publish > read.Index + read.Length);
        AssertReturningGuard(body[(read.Index + read.Length)..publish], "visit");
        Assert.Contains("UpdateCacheInfoAsync(", PageMethod("OnPageLoaded"), StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateCacheInfoAsync(", PageMethod("DataManagementSettingsPage"), StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmedTransfersDoNotAcquireAPageCancellationToken()
    {
        Assert.Matches(@"_transfer\.ExportBundleAsync\(\s*file\.Path\s*\)", PageMethod("OnExportBundleClick"));
        Assert.Matches(@"_transfer\.ImportBundleAsync\(\s*file\.Path\s*\)", PageMethod("OnImportBundleClick"));
        Assert.Matches(@"_transfer\.ExportJsonAsync\(\s*\)", PageMethod("OnExportClick"));
        Assert.Matches(@"_transfer\.ImportJsonAsync\(\s*json\s*\)", PageMethod("OnImportClick"));
        Assert.Matches(@"_transfer\.BackupAsync\(\s*\)", PageMethod("OnBackupNowClick"));
    }

    private static string CapturedVisit(string body)
    {
        var visit = Regex.Match(body, @"(?:var|int)\s+(\w+)\s*=\s*_visitVersion\s*;");
        Assert.True(visit.Success, "Capture the visit before awaiting a picker, dialog or transfer.");
        return visit.Groups[1].Value;
    }

    private static void AssertConfirmationVisitGuard(string body, string visit)
        => Assert.Matches(@"!\s*IsCurrentVisit\(\s*" + Regex.Escape(visit)
            + @"\s*\)[^;]*\)\s*(?:\{\s*)?return\s*;", body);

    private static void AssertReturningGuard(string body, string visit)
        => Assert.Matches(@"if\s*\([^;]*!\s*IsCurrentVisit\(\s*" + Regex.Escape(visit)
            + @"\s*\)[^;]*\)\s*(?:\{\s*)?return\s*;", body);

    private static string PageMethod(string name)
    {
        var path = Path.Combine(AppDataRootsTests.RepositoryRoot, "Plugins", "AniMeido.Plugin.Base",
            "Views", "DataManagementSettingsPage.xaml.cs");
        var source = Regex.Replace(File.ReadAllText(path), @"//[^\r\n]*", string.Empty);
        var start = Regex.Match(source, @"(?m)^ {8}(?:private|public)\s+[^\r\n{;=]*\b"
            + Regex.Escape(name) + @"\s*\(");
        Assert.True(start.Success, $"Data management page method not found: {name}");
        var remaining = source[(start.Index + start.Length)..];
        var next = Regex.Match(remaining, @"(?m)^ {8}(?:private|public)\s+");
        return next.Success
            ? source.Substring(start.Index, start.Length + next.Index)
            : source[start.Index..];
    }
}
