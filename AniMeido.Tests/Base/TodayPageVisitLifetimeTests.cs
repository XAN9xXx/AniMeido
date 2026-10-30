using System.Text.RegularExpressions;

namespace AniMeido.Tests;

[Trait("Category", "Mechanism")]
public sealed class TodayPageVisitLifetimeTests
{
    [Fact]
    public void LoadedSubscribesAndReadsCurrentAvailabilityBeforeWaitingForVisibilityRefresh()
    {
        var body = LoadedBody();
        var waiting = body.IndexOf("await ", StringComparison.Ordinal);
        var subscription = body.IndexOf("_playbackLauncher.AvailabilityChanged +=", StringComparison.Ordinal);
        var availability = body.IndexOf("_playbackLauncher.IsAvailable", StringComparison.Ordinal);
        var publication = Regex.Match(body, @"ViewModel\.IsPlaybackAvailable\s*=");

        Assert.True(waiting >= 0, "Loaded must await its visibility refresh.");
        Assert.True(subscription >= 0 && subscription < waiting,
            "Subscribe while this visit is still synchronous, not after its read has returned.");
        Assert.True(availability >= 0 && availability < waiting);
        Assert.True(publication.Success && publication.Index < waiting,
            "Returning to a cached page must observe availability changes that happened while unsubscribed.");
    }

    [Fact]
    public void VisibilityReadOwnsItsSourceAndUnloadOnlyCancelsTheCurrentVisit()
    {
        var loaded = LoadedBody();
        var owner = Regex.Match(loaded, @"using\s+var\s+(\w+)\s*=\s*new\s+CancellationTokenSource\s*\(");
        Assert.True(owner.Success, "The Loaded read must own and eventually release its own cancellation source.");
        var ownerName = Regex.Escape(owner.Groups[1].Value);
        var token = Regex.Match(loaded, @"var\s+(\w+)\s*=\s*" + ownerName + @"\.Token\s*;");
        Assert.True(token.Success, "Retain the read's own token across awaits.");
        var tokenName = Regex.Escape(token.Groups[1].Value);
        Assert.Matches(@"RemoveBlockedEntriesAsync\(\s*" + tokenName + @"\s*\)", loaded);
        Assert.Matches(@"finally\s*\{\s*if\s*\(\s*ReferenceEquals\(\s*_visibilityCancellation\s*,\s*"
            + ownerName + @"\s*\)\s*\)\s*(?:\{\s*)?_visibilityCancellation\s*=\s*null", loaded);

        var unloaded = PageMethod("private void OnUnloaded(", "private void OnPlaybackAvailabilityChanged(");
        var retired = Regex.Match(unloaded,
            @"Interlocked\.Increment\(\s*ref\s+_visitGeneration\s*\)|\+\+_visitGeneration|_visitGeneration\+\+");
        var cancel = unloaded.IndexOf("_visibilityCancellation?.Cancel()", StringComparison.Ordinal);
        var clear = unloaded.IndexOf("_visibilityCancellation = null", StringComparison.Ordinal);
        Assert.True(retired.Success && cancel > retired.Index && clear > cancel);
        Assert.DoesNotMatch(@"_visibilityCancellation\??\.Dispose\s*\(", loaded + unloaded);
    }

    [Fact]
    public void AvailabilityCallbackRejectsOldVisitsWithoutReadingControlsOffTheUiThread()
    {
        var body = PageMethod("private void OnPlaybackAvailabilityChanged(", "public async Task OnNavigatedToAsync(");
        var queue = body.IndexOf("DispatcherQueue.TryEnqueue(", StringComparison.Ordinal);
        Assert.True(queue >= 0);
        var beforeQueue = body[..queue];
        var callback = body[queue..];
        var visit = Regex.Match(beforeQueue,
            @"var\s+(\w+)\s*=\s*(?:Volatile\.Read\(\s*ref\s+_visitGeneration\s*\)|_visitGeneration)\s*;");
        Assert.True(visit.Success, "Capture the visit that received this event before queueing work.");
        Assert.DoesNotContain("IsLoaded", beforeQueue, StringComparison.Ordinal);
        Assert.Contains("_isPlaybackAvailabilitySubscribed", beforeQueue, StringComparison.Ordinal);
        Assert.Contains("return;", beforeQueue, StringComparison.Ordinal);

        var visitName = Regex.Escape(visit.Groups[1].Value);
        var current = @"(?:Volatile\.Read\(\s*ref\s+_visitGeneration\s*\)|_visitGeneration)";
        var identity = Regex.Match(callback, @"\b" + visitName + @"\s*!=\s*" + current
            + @"|" + current + @"\s*!=\s*\b" + visitName);
        var loaded = callback.IndexOf("IsLoaded", StringComparison.Ordinal);
        var publication = callback.IndexOf("ViewModel.IsPlaybackAvailable", StringComparison.Ordinal);
        var reload = callback.IndexOf("ReloadSafelyAsync()", StringComparison.Ordinal);
        var rejection = callback.IndexOf("return;", StringComparison.Ordinal);
        Assert.True(loaded >= 0 && identity.Success,
            "The UI callback must check both attachment and the captured visit.");
        Assert.True(rejection > loaded && rejection > identity.Index
            && publication > rejection && reload > rejection);
    }

    [Fact]
    public void ReturningVisitReloadsChangedAvailabilityWithoutDuplicatingFirstNavigation()
    {
        var body = LoadedBody();
        var changed = Regex.Match(body,
            @"(?:var|bool)\s+(\w+)\s*=\s*(?:ViewModel\.IsPlaybackAvailable\s*!=\s*[^;]+|[^;]+\s*!=\s*ViewModel\.IsPlaybackAvailable)\s*;");
        Assert.True(changed.Success, "Compare availability with the value retained by the cached page.");
        var changedName = Regex.Escape(changed.Groups[1].Value);
        Assert.Matches(@"ViewModel\.IsLoadInterrupted\s*\|\|\s*" + changedName
            + @"|\b" + changedName + @"\s*\|\|\s*ViewModel\.IsLoadInterrupted", body);
        var returning = Regex.Match(body, @"if\s*\(\s*_wasLoaded\s*\)");
        var reload = body.IndexOf("ReloadSafelyAsync()", StringComparison.Ordinal);
        var firstVisitRecorded = body.IndexOf("_wasLoaded = true", StringComparison.Ordinal);
        Assert.True(returning.Success && reload > returning.Index && firstVisitRecorded > reload,
            "Only an already loaded page restarts here; its first load belongs to navigation.");
        Assert.Contains("ViewModel.Theme.ResumeIfNeeded()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainLoadOwnsItsLinkedSourceUntilTheAwaitCompletes()
    {
        var reload = PageMethod("private async Task ReloadAsync()", "private void OnContentSizeChanged(");
        Assert.DoesNotContain("IsLoaded", reload, StringComparison.Ordinal);
        var hub = Regex.Match(reload, @"var\s+(\w+)\s*=\s*new\s+CancellationTokenSource\s*\(\s*\)\s*;");
        Assert.True(hub.Success, "Each reload starts a new cancellation period.");
        var hubName = Regex.Escape(hub.Groups[1].Value);
        var publication = Regex.Match(reload, @"_loadCancellation\s*=\s*" + hubName + @"\s*;");
        var owner = Regex.Match(reload, @"using\s+var\s+(\w+)\s*=\s*CancellationTokenSource\.CreateLinkedTokenSource\(\s*"
            + hubName + @"\.Token\s*\)\s*;");
        Assert.True(publication.Success && owner.Success && owner.Index > publication.Index,
            "Publish the current period, but give the awaited load its own linked source.");

        var ownerName = Regex.Escape(owner.Groups[1].Value);
        var token = Regex.Match(reload, @"var\s+(\w+)\s*=\s*" + ownerName + @"\.Token\s*;");
        Assert.True(token.Success && token.Index > owner.Index);
        var tokenName = Regex.Escape(token.Groups[1].Value);
        var waiting = Regex.Match(reload, @"await\s+ViewModel\.LoadAsync\(\s*" + tokenName + @"\s*\)\s*;");
        Assert.True(waiting.Success && waiting.Index > token.Index,
            "The operation owns its source through the entire main-load await.");
        Assert.DoesNotMatch(@"\b" + ownerName + @"\??\.Dispose\s*\(", reload);
        Assert.DoesNotMatch(@"\b" + hubName + @"\??\.Dispose\s*\(", reload);
    }

    [Fact]
    public void ReloadPublishesItsPeriodBeforeRetiringTheCapturedPreviousPeriod()
    {
        var reload = PageMethod("private async Task ReloadAsync()", "private void OnContentSizeChanged(");
        var previous = Regex.Match(reload, @"var\s+(\w+)\s*=\s*_loadCancellation\s*;");
        Assert.True(previous.Success, "Retire a captured source, not the mutable current field.");
        var previousName = Regex.Escape(previous.Groups[1].Value);
        var publication = Regex.Match(reload, @"_loadCancellation\s*=\s*\w+\s*;");
        var owner = reload.IndexOf("CancellationTokenSource.CreateLinkedTokenSource(", StringComparison.Ordinal);
        var cancel = Regex.Match(reload, @"\b" + previousName + @"\??\.Cancel\(\s*\)");
        Assert.True(publication.Success && publication.Index > previous.Index
            && owner > publication.Index && cancel.Success && cancel.Index > owner,
            "A cancellation callback may reenter reload; publish and link this period before invoking it.");
        Assert.Matches(@"try\s*\{\s*" + previousName + @"\??\.Cancel\(\s*\)\s*;\s*\}\s*finally\s*\{\s*"
            + previousName + @"\??\.Dispose\(\s*\)\s*;\s*\}", reload);
        Assert.DoesNotMatch(@"_loadCancellation\??\.(?:Cancel|Dispose)\s*\(", reload);

        var ownerToken = Regex.Match(reload, @"var\s+(\w+)\s*=\s*\w+\.Token\s*;");
        Assert.True(ownerToken.Success);
        var tokenName = Regex.Escape(ownerToken.Groups[1].Value);
        var canceled = Regex.Match(reload, @"\b" + tokenName + @"\.ThrowIfCancellationRequested\(\s*\)\s*;");
        var waiting = Regex.Match(reload, @"await\s+ViewModel\.LoadAsync\(\s*" + tokenName + @"\s*\)");
        Assert.True(canceled.Success && canceled.Index > cancel.Index
            && waiting.Success && waiting.Index > canceled.Index,
            "A period superseded by a reentrant callback must not start an obsolete VM load.");
    }

    [Fact]
    public void UnloadRetiresTheCapturedPeriodAndContinuesCleanupAfterCancellationCallbackFailure()
    {
        var unloaded = PageMethod("private void OnUnloaded(", "private void OnPlaybackAvailabilityChanged(");
        var previous = Regex.Match(unloaded, @"var\s+(\w+)\s*=\s*_loadCancellation\s*;");
        Assert.True(previous.Success);
        var previousName = Regex.Escape(previous.Groups[1].Value);
        var clear = unloaded.IndexOf("_loadCancellation = null", StringComparison.Ordinal);
        var cancel = Regex.Match(unloaded, @"\b" + previousName + @"\??\.Cancel\(\s*\)");
        Assert.True(clear > previous.Index && cancel.Success && cancel.Index > clear,
            "Clear the current field before callbacks run, then retire only the captured source.");
        Assert.Matches(@"try\s*\{\s*" + previousName + @"\??\.Cancel\(\s*\)\s*;\s*\}\s*"
            + @"(?:catch\s*\(\s*AggregateException\s+\w+\s*\)\s*\{[^{}]*\}\s*)?finally\s*\{\s*"
            + previousName + @"\??\.Dispose\(\s*\)\s*;\s*\}", unloaded);
        Assert.DoesNotMatch(@"_loadCancellation\??\.(?:Cancel|Dispose)\s*\(", unloaded);

        var failure = Regex.Match(unloaded, @"catch\s*\(\s*AggregateException\s+(\w+)\s*\)\s*\{\s*_logger\.LogWarning\(\s*\1\s*,");
        Assert.True(failure.Success && failure.Index > cancel.Index,
            "The page's existing logger must observe a failed cancellation callback.");
        Assert.True(unloaded.IndexOf("ViewModel.Theme.CancelLoad()", StringComparison.Ordinal) > failure.Index);
        Assert.True(unloaded.IndexOf("ViewModel.SuspendPlanDetails()", StringComparison.Ordinal) > failure.Index);
        Assert.True(unloaded.IndexOf("_playbackLauncher.AvailabilityChanged -=", StringComparison.Ordinal) > failure.Index,
            "A failed cancellation callback must not skip the remaining page cleanup.");
    }

    [Fact]
    public void SuccessfulStatusWriteOwnsItsRefreshSourceFromTheThenCurrentPeriod()
    {
        var status = PageMethod("private async Task ApplyThemeStatusSafelyAsync(", "private void OnThemeRowPointerEntered(");
        var write = Regex.Match(status, @"if\s*\(\s*!await\s+ViewModel\.Theme\.ApplyStatusAsync\(\s*item\s*,\s*status\s*\)\s*\)\s*return\s*;");
        Assert.True(write.Success, "Only a completed status write starts a refresh.");
        var owner = Regex.Match(status, @"using\s+var\s+(\w+)\s*=\s*CancellationTokenSource\.CreateLinkedTokenSource\(\s*"
            + @"_loadCancellation\?\.Token\s*\?\?\s*CancellationToken\.None\s*\)\s*;");
        Assert.True(owner.Success && owner.Index > write.Index + write.Length,
            "Select the current period after the write; preserve the existing no-period fallback.");
        Assert.DoesNotContain("_loadCancellation", status[..owner.Index], StringComparison.Ordinal);

        var ownerName = Regex.Escape(owner.Groups[1].Value);
        var token = Regex.Match(status, @"var\s+(\w+)\s*=\s*" + ownerName + @"\.Token\s*;");
        Assert.True(token.Success && token.Index > owner.Index);
        var tokenName = Regex.Escape(token.Groups[1].Value);
        var waiting = Regex.Match(status, @"await\s+ViewModel\.RefreshAfterStatusChangeAsync\(\s*" + tokenName + @"\s*\)\s*;");
        Assert.True(waiting.Success && waiting.Index > token.Index,
            "Keep the refresh's source alive until its actual work has completed.");
        Assert.DoesNotMatch(@"\b" + ownerName + @"\??\.Dispose\s*\(", status);
    }

    private static string LoadedBody()
        => PageMethod("private async void OnLoaded(", "private void OnUnloaded(");

    private static string PageMethod(string start, string next)
    {
        var path = Path.Combine(AppDataRootsTests.RepositoryRoot, "Plugins", "AniMeido.Plugin.Base",
            "Views", "TodayPage.xaml.cs");
        var source = File.ReadAllText(path);
        var first = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(first >= 0, $"Today page method not found: {start}");
        var last = source.IndexOf(next, first + start.Length, StringComparison.Ordinal);
        Assert.True(last > first, $"Today page method not found: {next}");
        return Regex.Replace(source[first..last], @"//[^\r\n]*", string.Empty);
    }
}
