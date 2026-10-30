using System.Text.RegularExpressions;

namespace AniMeido.Tests;

// 检查真实页面的访问、确认和写后刷新接线；原生对话框交互由页面验收覆盖。
[Trait("Category", "Mechanism")]
public sealed class TodayPageDialogLifetimeTests
{
    [Fact]
    public void LeavingInvalidatesTheVisitBeforeHidingTheOwnedDialog()
    {
        var unloaded = PageMethod("OnUnloaded");
        var invalidation = Regex.Match(unloaded, @"Interlocked\.Increment\(\s*ref\s+_visitGeneration\s*\)|\+\+_visitGeneration|_visitGeneration\+\+");
        var captured = Regex.Match(unloaded, @"var\s+(\w+)\s*=\s*_activeDialog\s*;");
        var owner = captured.Success ? Regex.Escape(captured.Groups[1].Value) : "_activeDialog";
        var hide = Regex.Match(unloaded, @"\b" + owner + @"\??\.Hide\(\s*\)");
        Assert.True(invalidation.Success && hide.Success && hide.Index > invalidation.Index);
        Assert.DoesNotMatch(@"\b_activeDialog\s*=\s*null\s*;", unloaded);

        var current = PageMethod("IsCurrentVisit");
        Assert.Contains("IsLoaded", current, StringComparison.Ordinal);
        Assert.Matches(@"(?:visit\s*==\s*(?:Volatile\.Read\(\s*ref\s+_visitGeneration\s*\)|_visitGeneration)|(?:Volatile\.Read\(\s*ref\s+_visitGeneration\s*\)|_visitGeneration)\s*==\s*visit)", current);
    }

    [Fact]
    public void DialogOwnerChecksTheVisitAndRootAndRetainsItsReferenceUntilShowingEnds()
    {
        var body = PageMethod("ShowDialogAsync");
        Assert.Matches(@"ContentDialog\s+dialog\s*,\s*int\s+visit", body);
        var showing = body.IndexOf("await dialog.ShowAsync()", StringComparison.Ordinal);
        var ownership = Regex.Match(body, @"_activeDialog\s*=\s*dialog\s*;");
        Assert.True(showing >= 0 && ownership.Success && ownership.Index < showing);
        var admission = body[..ownership.Index];
        Assert.Contains("IsCurrentVisit(visit)", admission, StringComparison.Ordinal);
        Assert.Matches(@"if\s*\([^;]*XamlRoot[^;]*\)\s*(?:\{\s*)?return\s+ContentDialogResult.None\s*;", admission);
        Assert.DoesNotContain("throw new InvalidOperationException", admission, StringComparison.Ordinal);
        Assert.Contains("IsCurrentVisit(visit)", body[showing..], StringComparison.Ordinal);
        var cleanup = body.IndexOf("finally", showing, StringComparison.Ordinal);
        var ownReference = Regex.Match(body, @"ReferenceEquals\(\s*(?:_activeDialog\s*,\s*dialog|dialog\s*,\s*_activeDialog)\s*\)");
        var clearing = body.LastIndexOf("_activeDialog = null", StringComparison.Ordinal);
        Assert.True(cleanup > showing && ownReference.Success && ownReference.Index > cleanup
            && clearing > ownReference.Index);
    }

    [Theory]
    [InlineData("OnClearBrowseHistoryClick")]
    [InlineData("OnEditPlanClick")]
    [InlineData("OnAddReminderClick")]
    [InlineData("OnManageRemindersClick")]
    public void DialogEntriesShareAdmissionBeforeTheirFirstAwait(string method)
    {
        var body = PageMethod(method);
        var firstAwait = body.IndexOf("await ", StringComparison.Ordinal);
        Assert.True(firstAwait > 0);
        var beforeAwait = body[..firstAwait];
        Assert.Contains("using var operation = _dialogOperation.TryBegin()", beforeAwait, StringComparison.Ordinal);
        Assert.Matches(@"if\s*\(operation is null\)\s*return;", beforeAwait);
    }

    [Theory]
    [InlineData("OnClearBrowseHistoryClick", "ViewModel.ClearBrowseHistoryCommand.ExecuteAsync(")]
    [InlineData("OnEditPlanClick", "_actionCenter.UpsertPlanAsync(")]
    [InlineData("OnAddReminderClick", "_reminders.AddRelativeReminderAsync(")]
    [InlineData("OnManageRemindersClick", "_reminders.RemoveReminderAsync(")]
    public void ConfirmationAcceptsOnlyTheVisitThatOpenedIt(string method, string mutation)
    {
        var body = PageMethod(method);
        var visit = CapturedVisit(body);
        var confirmation = Regex.Match(body, @"await\s+ShowDialogAsync\(\s*\w+\s*,\s*" + Regex.Escape(visit) + @"\s*\)");
        var writing = body.IndexOf(mutation, StringComparison.Ordinal);
        Assert.True(confirmation.Success && writing > confirmation.Index + confirmation.Length);
        var acceptance = body[(confirmation.Index + confirmation.Length)..writing];
        Assert.Contains("ContentDialogResult.Primary", acceptance, StringComparison.Ordinal);
        AssertVisitGuard(acceptance, visit);
        Assert.DoesNotMatch(@"await\s+\w+\.ShowAsync\(\s*\)", body);
        Assert.DoesNotMatch(@"_loadCancellation|_visibilityCancellation|CancellationToken", body[writing..]);
    }

    [Fact]
    public void ReminderReadChecksItsVisitBeforeShowingEitherEmptyFeedbackOrTheDialog()
    {
        var body = PageMethod("OnManageRemindersClick");
        var visit = CapturedVisit(body);
        var reading = body.IndexOf("await _actionCenter.GetRemindersAsync(", StringComparison.Ordinal);
        var readEnd = body.IndexOf(';', reading);
        var empty = body.IndexOf("if (reminders.Count == 0)", readEnd, StringComparison.Ordinal);
        var dialog = body.IndexOf("new ContentDialog", readEnd, StringComparison.Ordinal);
        Assert.True(reading >= 0 && readEnd > reading && empty > readEnd && dialog > empty);
        AssertVisitGuard(body[(readEnd + 1)..empty], visit);
    }

    [Theory]
    [InlineData("OnEditPlanClick", "_reminders.RescheduleAnimeAsync(")]
    [InlineData("OnAddReminderClick", "_reminders.AddAbsoluteReminderAsync(")]
    [InlineData("OnManageRemindersClick", "_reminders.RemoveReminderAsync(")]
    public void CompletedMutationRefreshesOnlyItsOriginalVisit(string method, string mutation)
    {
        var body = PageMethod(method);
        var visit = CapturedVisit(body);
        var writing = body.IndexOf(mutation, StringComparison.Ordinal);
        var refresh = Regex.Match(body, @"await\s+ReloadSafelyAsync\(\s*" + Regex.Escape(visit) + @"\s*\)");
        Assert.True(writing >= 0 && refresh.Success && refresh.Index > writing);
        Assert.DoesNotContain("await ReloadSafelyAsync()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptedPlanEditKeepsTheWholePersistenceAndReminderSequence()
    {
        var body = PageMethod("OnEditPlanClick");
        var save = body.IndexOf("await _actionCenter.UpsertPlanAsync(", StringComparison.Ordinal);
        var read = body.IndexOf("await _actionCenter.GetPlanAsync(", StringComparison.Ordinal);
        var reschedule = body.IndexOf("await _reminders.RescheduleAnimeAsync(", StringComparison.Ordinal);
        Assert.True(save >= 0 && read > save && reschedule > read);
        Assert.DoesNotContain("IsCurrentVisit(", body[save..reschedule], StringComparison.Ordinal);
        Assert.DoesNotMatch(@"_loadCancellation|_visibilityCancellation|CancellationToken", body[save..]);
    }

    [Fact]
    public void ReminderFailureFeedbackBelongsToTheOriginalVisit()
    {
        var body = PageMethod("OnAddReminderClick");
        var visit = CapturedVisit(body);
        var failure = Regex.Match(body, @"catch\s*\(\s*InvalidOperationException\s+\w+\s*\)");
        Assert.True(failure.Success);
        var feedback = body.IndexOf("ShowNotification(", failure.Index, StringComparison.Ordinal);
        Assert.True(feedback > failure.Index);
        var branch = body[failure.Index..feedback];
        Assert.Matches(@"if\s*\([^;]*IsCurrentVisit\(\s*" + Regex.Escape(visit) + @"\s*\)[^;]*\)", branch);
    }

    [Fact]
    public void VisitAwareReloadChecksBeforeReadingAndBeforePublishingAFailure()
    {
        var body = PageMethod("ReloadSafelyAsync");
        Assert.Matches(@"int\?\s+visit\s*=\s*null", body);
        var reading = body.IndexOf("await ReloadAsync()", StringComparison.Ordinal);
        Assert.True(reading >= 0);
        Assert.Matches(@"visit\s+is\s*\{\s*\}\s+\w+", body[..reading]);
        Assert.Matches(@"if\s*\([^;]*!\s*IsCurrentVisit\([^;]*\)[^;]*\)\s*(?:\{\s*)?return\s*;", body[..reading]);
        var failure = body.IndexOf("catch (Exception", reading, StringComparison.Ordinal);
        Assert.True(failure > reading);
        var feedback = body.IndexOf("ShowNotification(", failure, StringComparison.Ordinal);
        Assert.True(feedback > failure);
        Assert.Matches(@"if\s*\([^;]*IsCurrentVisit\([^;]*\)[^;]*\)\s*(?:\{\s*)?ShowNotification\(", body[failure..]);
        Assert.Matches(@"visit\s+is\s+null\s*\|\|\s*IsCurrentVisit\(\s*visit\.Value\s*\)", body[failure..feedback]);
    }

    private static string CapturedVisit(string body)
    {
        var captured = Regex.Match(body, @"(?:var|int)\s+(\w+)\s*=\s*(?:Volatile\.Read\(\s*ref\s+_visitGeneration\s*\)|_visitGeneration)\s*;");
        var firstAwait = body.IndexOf("await ", StringComparison.Ordinal);
        Assert.True(captured.Success && firstAwait > captured.Index,
            "Capture the page visit before its first asynchronous operation.");
        AssertVisitGuard(body[(captured.Index + captured.Length)..firstAwait], captured.Groups[1].Value);
        return captured.Groups[1].Value;
    }

    private static void AssertVisitGuard(string body, string visit)
        => Assert.Matches(@"if\s*\([^;]*!\s*IsCurrentVisit\(\s*" + Regex.Escape(visit)
            + @"\s*\)[^;]*\)\s*(?:\{\s*)?return\s*;", body);

    private static string PageMethod(string name)
    {
        var path = Path.Combine(AppDataRootsTests.RepositoryRoot, "Plugins", "AniMeido.Plugin.Base",
            "Views", "TodayPage.xaml.cs");
        var source = Regex.Replace(File.ReadAllText(path), @"//[^\r\n]*", string.Empty);
        var start = Regex.Match(source, @"(?m)^ {4}(?:private|public)\s+[^\r\n{;=]*\b" + Regex.Escape(name) + @"\s*\(");
        Assert.True(start.Success, $"Today page method not found: {name}");
        var remaining = source[(start.Index + start.Length)..];
        var next = Regex.Match(remaining, @"(?m)^ {4}(?:private|public)\s+");
        return next.Success ? source.Substring(start.Index, start.Length + next.Index) : source[start.Index..];
    }
}
