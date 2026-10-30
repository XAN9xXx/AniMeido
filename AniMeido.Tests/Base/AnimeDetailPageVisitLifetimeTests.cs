using System.Text.RegularExpressions;

namespace AniMeido.Tests;

// 检查真实页面的访问与释放接线；原生页面交错仍按实际验收记录。
[Trait("Category", "Mechanism")]
public sealed class AnimeDetailPageVisitLifetimeTests
{
    [Fact]
    public void NavigationOwnsItsCancellationSourceUntilBothReadsFinish()
    {
        var body = PageMethod("public async Task OnNavigatedToAsync(", "private bool _browseRecorded;");
        var owner = Regex.Match(body, @"var\s+(\w+)\s*=\s*new\s+CancellationTokenSource\s*\(\s*\)\s*;");
        Assert.True(owner.Success, "The awaited navigation must retain its own cancellation source.");
        var name = Regex.Escape(owner.Groups[1].Value);
        Assert.Matches(@"_navigationLoadCancellation\s*=\s*" + name + @"\s*;", body);
        var waiting = body.IndexOf("await Task.WhenAll(", StringComparison.Ordinal);
        var cleanup = body.IndexOf("finally", StringComparison.Ordinal);
        Assert.True(waiting > owner.Index && cleanup > waiting);
        var final = body[cleanup..];
        Assert.Matches(@"\b" + name + @"\.Dispose\(\s*\)", final);
        Assert.Matches(@"ReferenceEquals\(\s*_navigationLoadCancellation\s*,\s*" + name + @"\s*\)", final);
        Assert.Contains("_navigationLoadCancellation = null", final, StringComparison.Ordinal);
    }

    [Fact]
    public void LeavingOrReplacingNavigationRetiresTheVisitAndOnlyCancelsItsRead()
    {
        var unloaded = PageMethod("private void OnPageUnloaded(", "private void UpdatePlaybackAvailability(");
        var navigation = PageMethod("public async Task OnNavigatedToAsync(", "private bool _browseRecorded;");
        const string nextVisit = @"(?:\+\+_navigationVersion|_navigationVersion\+\+|Interlocked\.Increment\(\s*ref\s+_navigationVersion\s*\))";

        Assert.Matches(nextVisit, unloaded);
        Assert.Matches(nextVisit, navigation);
        var retirement = Regex.Match(unloaded, nextVisit);
        Assert.True(unloaded.IndexOf("Cancel()", StringComparison.Ordinal) > retirement.Index);
        Assert.DoesNotMatch(@"_navigationLoadCancellation\??\.Dispose\(\s*\)", unloaded + navigation);
    }

    [Fact]
    public void LeavingAlsoRemembersAnActiveManualRetry()
    {
        var unloaded = PageMethod("private void OnPageUnloaded(", "private void UpdatePlaybackAvailability(");
        Assert.Contains("ViewModel.LoadDetailCommand.IsRunning", unloaded, StringComparison.Ordinal);
    }

    [Fact]
    public void SavedTagReadFailureChecksTheOriginalVisitBeforeShowingItsMessage()
    {
        var body = MethodContaining("await _savedTagService!.GetAllSavedTagsAsync(");
        var read = body.IndexOf("await _savedTagService", StringComparison.Ordinal);
        var failure = body.IndexOf("catch (Exception ex)", read, StringComparison.Ordinal);
        var publication = body.IndexOf("ErrorInfoBar.Message =", failure, StringComparison.Ordinal);
        Assert.True(read >= 0 && failure > read && publication > failure);
        var header = body[..body.IndexOf('{')];
        var visit = Regex.Match(header, @"\bint\s+(?!animeId\b)(\w+)");
        if (!visit.Success)
            visit = Regex.Match(body[..read], @"var\s+(\w+)\s*=\s*_navigationVersion\s*;");
        Assert.True(visit.Success, "Retain the visit that started this tag read.");
        AssertRejectsOldVisit(body[failure..publication], visit.Groups[1].Value, "animeId");
        Assert.Matches(@"_logger\.LogWarning\(\s*ex\s*,", body[failure..publication]);
        Assert.Single(Regex.Matches(body[failure..publication], @"_logger\.LogWarning\("));
    }

    [Fact]
    public void ReturningLoadedResumesBeforeTheOptionalPlayerEarlyReturn()
    {
        var body = PageMethod("private void OnPageLoaded(", "private void OnPageUnloaded(");
        var optionalPlayer = body.IndexOf("if (_playbackLauncher", StringComparison.Ordinal);
        Assert.True(optionalPlayer >= 0);
        var activation = body[..optionalPlayer];
        var returning = Regex.Match(activation, @"if\s*\(\s*(?<flag>_\w+)\s*\)");
        Assert.True(returning.Success, "A retained page resumes only after its first Loaded.");
        var resume = activation.IndexOf("ResumePageAsync()", StringComparison.Ordinal);
        var remembered = activation.IndexOf(
            returning.Groups["flag"].Value + " = true", StringComparison.Ordinal);
        Assert.True(resume > returning.Index && remembered > resume);
    }

    [Fact]
    public void ReturningPageResumesInterruptedReadsAndOtherwiseRefreshesOnlyLocalTags()
    {
        var body = MethodContaining("private async Task ResumePageAsync(");
        var interrupted = Regex.Match(body, @"if\s*\(\s*(_\w+)\s*\)");
        Assert.True(interrupted.Success, "Interrupted work needs a separate return path.");
        var mainLoad = MethodContaining("await Task.WhenAll(");
        var mainName = Regex.Match(mainLoad, @"(?:Task|Task<[^>]+>)\s+(\w+)\s*\(");
        var localRead = MethodContaining("await _savedTagService!.GetAllSavedTagsAsync(");
        var localName = Regex.Match(localRead, @"(?:Task|Task<[^>]+>)\s+(\w+)\s*\(");
        Assert.True(mainName.Success && localName.Success);
        var reload = body.IndexOf(mainName.Groups[1].Value + "(", StringComparison.Ordinal);
        var refresh = body.IndexOf(localName.Groups[1].Value + "(", StringComparison.Ordinal);
        Assert.True(reload > interrupted.Index && refresh > reload);
        Assert.Contains("return;", body[reload..refresh], StringComparison.Ordinal);
        Assert.DoesNotContain("_browseRecorded = false", body, StringComparison.Ordinal);
        if (mainLoad.Contains("_browseRecorded = false", StringComparison.Ordinal))
        {
            var reset = Regex.Match(mainLoad, @"\bbool\s+(\w+)\s*[,)]");
            Assert.True(reset.Success);
            Assert.Contains(reset.Groups[1].Value + ": false", body, StringComparison.Ordinal);
            Assert.Matches(@"if\s*\(\s*" + Regex.Escape(reset.Groups[1].Value)
                + @"\s*\)\s*(?:\{\s*)?_browseRecorded\s*=\s*false", mainLoad);
        }
        Assert.DoesNotContain("OnNavigatedToAsync(", body, StringComparison.Ordinal);
        Assert.Contains("TagContainer.Children", body + localRead, StringComparison.Ordinal);
        Assert.Contains(".Tag", body + localRead, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalTagRefreshHoldsThePageGateUntilTheReadAndPublicationFinish()
    {
        var body = MethodContaining("await _savedTagService!.GetAllSavedTagsAsync(");
        var gate = body.IndexOf("await _tagStateGate.WaitAsync(", StringComparison.Ordinal);
        var read = body.IndexOf("await _savedTagService!.GetAllSavedTagsAsync(", StringComparison.Ordinal);
        var publication = body.IndexOf("_savedTagNames.Clear()", StringComparison.Ordinal);
        var release = body.IndexOf("_tagStateGate.Release()", StringComparison.Ordinal);
        Assert.True(gate >= 0 && read > gate && publication > read && release > publication);
        Assert.Matches(@"finally\s*\{\s*_tagStateGate\.Release\(\s*\)\s*;", body);
        Assert.DoesNotContain("GetTagsAsync(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadDetailCommand", body[gate..release], StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmedTagWritesKeepTheirIntentAndGateThroughBothOutcomePaths()
    {
        var body = RightTapBody();
        var intent = body.IndexOf("_savedTagNames.Contains(name)", StringComparison.Ordinal);
        var gate = body.IndexOf("await _tagStateGate.WaitAsync(", StringComparison.Ordinal);
        var save = body.IndexOf("await _savedTagService!.SaveTagAsync(name)", StringComparison.Ordinal);
        var remove = body.IndexOf("await _savedTagService!.RemoveTagAsync(name)", StringComparison.Ordinal);
        var failure = body.IndexOf("收藏标签更新失败", StringComparison.Ordinal);
        var release = body.IndexOf("_tagStateGate.Release()", StringComparison.Ordinal);
        Assert.True(intent >= 0 && gate > intent && save > gate && remove > gate
            && failure > save && release > failure);
        Assert.Matches(@"finally\s*\{\s*_tagStateGate\.Release\(\s*\)\s*;", body);
    }

    [Theory]
    [InlineData("RemoveTagAsync", "_savedTagNames.Remove(name)")]
    [InlineData("SaveTagAsync", "_savedTagNames.Add(name)")]
    public void ConfirmedTagSaveFinishesBeforePublishingOnlyToItsOriginalVisit(
        string operation, string publication)
    {
        var body = RightTapBody();
        var captured = Regex.Match(body, @"var\s+(\w+)\s*=\s*_navigationVersion\s*;");
        Assert.True(captured.Success, "Keep the visit that accepted this confirmed tag change.");
        var anime = Regex.Match(body, @"var\s+(\w+)\s*=\s*_currentAnimeId\s*;");
        Assert.True(anime.Success);
        var save = body.IndexOf("await _savedTagService!." + operation + "(name)", StringComparison.Ordinal);
        var publish = body.IndexOf(publication, StringComparison.Ordinal);
        Assert.True(save > captured.Index && publish > save);
        Assert.Contains("IsLoaded", body[..save], StringComparison.Ordinal);
        var publicationGuard = body[save..publish];
        Assert.Contains("IsLoaded", publicationGuard, StringComparison.Ordinal);
        AssertRejectsOldVisit(publicationGuard, captured.Groups[1].Value, anime.Groups[1].Value);
    }

    [Fact]
    public void TagSaveFailureChecksTheOriginalVisitBeforeShowingItsMessage()
    {
        var body = RightTapBody();
        var captured = Regex.Match(body, @"var\s+(\w+)\s*=\s*_navigationVersion\s*;");
        Assert.True(captured.Success);
        var anime = Regex.Match(body, @"var\s+(\w+)\s*=\s*_currentAnimeId\s*;");
        Assert.True(anime.Success);
        var failure = body.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
        var publication = body.IndexOf("ErrorInfoBar.Message =", failure, StringComparison.Ordinal);
        Assert.True(failure > captured.Index && publication > failure);
        var publicationGuard = body[failure..publication];
        Assert.Contains("IsLoaded", publicationGuard, StringComparison.Ordinal);
        AssertRejectsOldVisit(publicationGuard, captured.Groups[1].Value, anime.Groups[1].Value);
        var log = publicationGuard.IndexOf("_logger.LogError(ex,", StringComparison.Ordinal);
        var guard = publicationGuard.IndexOf("IsCurrentNavigation(", StringComparison.Ordinal);
        Assert.True(log >= 0 && guard > log);
        Assert.Single(Regex.Matches(publicationGuard, @"_logger\.LogError\("));
    }

    [Fact]
    public void InitialNavigationIdentityWorksBeforeLoadedAndStillChecksVisitAndAnime()
    {
        var method = Regex.Match(ReadPageSource(),
            @"private\s+bool\s+IsCurrentNavigation\(\s*int\s+(\w+)\s*,\s*int\s+(\w+)\s*\)(.*?)(?=\r?\n\s*(?:private|public|internal)\s|\z)",
            RegexOptions.Singleline);
        Assert.True(method.Success);
        var body = method.Groups[3].Value;
        var visit = Regex.Escape(method.Groups[1].Value);
        var anime = Regex.Escape(method.Groups[2].Value);
        Assert.DoesNotContain("IsLoaded", body, StringComparison.Ordinal);
        Assert.Matches(@"\b" + visit + @"\s*==\s*_navigationVersion|_navigationVersion\s*==\s*\b" + visit, body);
        Assert.Matches(@"\b" + anime + @"\s*==\s*_currentAnimeId|_currentAnimeId\s*==\s*\b" + anime, body);
    }

    private static void AssertRejectsOldVisit(string body, string capturedVisit, string capturedAnime)
    {
        var visit = Regex.Escape(capturedVisit);
        var anime = Regex.Escape(capturedAnime);
        Assert.Matches(@"if\s*\(\s*(?:!IsLoaded\s*\|\|\s*)?!IsCurrentNavigation\(\s*" + visit
            + @"\s*,\s*" + anime + @"\s*\)(?:\s*\|\|\s*!IsLoaded)?\s*\)\s*(?:\{\s*)?return\s*;", body);
    }

    private static string RightTapBody()
    {
        var body = PageMethod("private Border CreateTagButton(", "return border;");
        var callback = body.IndexOf("border.RightTapped +=", StringComparison.Ordinal);
        Assert.True(callback >= 0);
        return body[callback..];
    }

    private static string PageMethod(string start, string next)
    {
        var source = ReadPageSource();
        var first = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(first >= 0, $"Detail page source not found: {start}");
        var last = source.IndexOf(next, first + start.Length, StringComparison.Ordinal);
        Assert.True(last > first, $"Detail page source not found: {next}");
        return source[first..last];
    }

    private static string ReadPageSource()
        => File.ReadAllText(Path.Combine(
            AppDataRootsTests.RepositoryRoot, "Plugins", "AniMeido.Plugin.Base",
            "Views", "AnimeDetailPage.xaml.cs"));

    private static string MethodContaining(string marker)
    {
        var source = ReadPageSource();
        var methods = Regex.Matches(source,
            @"(?m)^        (?:private|public|internal)\s+(?:static\s+)?(?:async\s+)?[\w<>?]+\s+\w+\s*\(");
        for (var index = 0; index < methods.Count; index++)
        {
            var first = methods[index].Index;
            var last = index + 1 < methods.Count ? methods[index + 1].Index : source.Length;
            var method = source[first..last];
            if (method.Contains(marker, StringComparison.Ordinal))
                return method;
        }

        Assert.Fail($"Detail page method containing {marker} was not found.");
        return string.Empty;
    }
}
