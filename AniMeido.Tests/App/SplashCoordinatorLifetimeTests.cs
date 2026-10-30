using System.Text.RegularExpressions;

namespace AniMeido.Tests;

// 检查真实开屏事件的接线和释放，不构造原生 BitmapImage，也不模拟图片解码。
[Trait("Category", "Mechanism")]
public sealed class SplashCoordinatorLifetimeTests
{
    [Fact]
    public void LoadingRegistersItsCompletionAndNamedEventsBeforeAssigningTheImageUri()
    {
        var body = Method("LoadSplashImage");
        var missing = Regex.Match(body, @"if\s*\(\s*!System\.IO\.File\.Exists\(\s*\w+\s*\)\s*\)\s*(?:\{\s*)?return\s*;");
        var image = Regex.Match(body, @"_loadingImage\s*=\s*new\s+BitmapImage\(\s*\)\s*;");
        var completion = Regex.Match(body, @"_imageLoad\s*=\s*new\s+TaskCompletionSource(?:<[^>]+>)?\s*\(");
        var opened = NamedBinding(body, "ImageOpened");
        var failed = NamedBinding(body, "ImageFailed");
        var uri = body.IndexOf(".UriSource =", StringComparison.Ordinal);
        Assert.True(missing.Success && image.Success && image.Index > missing.Index);
        Assert.True(completion.Success && completion.Index < opened.Index && completion.Index < failed.Index);
        Assert.True(opened.Index > image.Index && failed.Index > image.Index
            && uri > opened.Index && uri > failed.Index);
        Assert.Contains("_splashImage.Source = _loadingImage", body, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadingAnotherImageDetachesBothHandlersFromThePreviousImage()
    {
        var body = Method("LoadSplashImage");
        var image = Regex.Match(body, @"_loadingImage\s*=\s*new\s+BitmapImage\(\s*\)\s*;");
        Assert.True(image.Success);
        var beforeReplacing = body[..image.Index];
        var wake = Regex.Match(beforeReplacing, @"_imageLoad\??\.TrySetResult\(\s*\)\s*;");
        var newCompletion = body.IndexOf("_imageLoad = new TaskCompletionSource", StringComparison.Ordinal);
        Assert.True(wake.Success && newCompletion > wake.Index,
            "Replacing the image completes the previous wait before taking ownership of a new completion source.");
        var oldImage = Regex.Match(beforeReplacing, @"_loadingImage\s+is\s*\{\s*\}\s+(\w+)");
        var receiver = oldImage.Success ? Regex.Escape(oldImage.Groups[1].Value) : "_loadingImage";
        foreach (var eventName in new[] { "ImageOpened", "ImageFailed" })
        {
            var handler = NamedBinding(body, eventName).Groups[1].Value;
            Assert.Matches(@"\b" + receiver + @"\." + eventName + @"\s*-=\s*"
                + Regex.Escape(handler) + @"\s*;", beforeReplacing);
        }
    }

    [Theory]
    [InlineData("ImageOpened")]
    [InlineData("ImageFailed")]
    public void CompletionEventsOnlyCompleteTheCurrentlyOwnedImage(string eventName)
    {
        var binding = NamedBinding(Method("LoadSplashImage"), eventName);
        var handler = Method(binding.Groups[1].Value);
        const string identity = @"ReferenceEquals\(\s*(?:sender\s*,\s*_loadingImage|_loadingImage\s*,\s*sender)\s*\)";
        Assert.Matches(@"(?:if\s*\(\s*" + identity + @"\s*\)\s*(?:\{\s*)?|if\s*\(\s*!"
            + identity + @"\s*\)\s*(?:\{\s*)?return\s*;\s*(?:\}\s*)?)_imageLoad\??\.TrySetResult\(\s*\)", handler);
        Assert.DoesNotContain("new TaskCompletionSource", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void WaitingUsesTheCapturedImageTaskAndAlwaysDetachesThatImagesHandlers()
    {
        var body = Method("WaitForImageAsync");
        var image = Regex.Match(body, @"var\s+(\w+)\s*=\s*_loadingImage\s*;");
        var completion = Regex.Match(body, @"var\s+(\w+)\s*=\s*_imageLoad\??\.Task\s*;");
        Assert.True(image.Success && completion.Success);
        var capturedImage = Regex.Escape(image.Groups[1].Value);
        var capturedTask = Regex.Escape(completion.Groups[1].Value);
        var waiting = Regex.Match(body, @"await\s+Task\.WhenAny\(\s*" + capturedTask
            + @"\s*,\s*Task\.Delay\(\s*5000\s*\)\s*\)\s*;");
        var cleanup = body.IndexOf("finally", StringComparison.Ordinal);
        Assert.True(waiting.Success && waiting.Index > image.Index && waiting.Index > completion.Index
            && cleanup > waiting.Index);
        Assert.Contains("try", body[..waiting.Index], StringComparison.Ordinal);
        Assert.Matches(@"if\s*\([^;]*" + capturedImage + @"\s+is\s+null[^;]*\)\s*(?:\{\s*)?return\s*;",
            body[..waiting.Index]);
        foreach (var eventName in new[] { "ImageOpened", "ImageFailed" })
        {
            var handler = NamedBinding(Method("LoadSplashImage"), eventName).Groups[1].Value;
            Assert.Matches(@"\b" + capturedImage + @"\." + eventName + @"\s*-=\s*"
                + Regex.Escape(handler) + @"\s*;", body[cleanup..]);
        }
        Assert.DoesNotMatch(@"\.Image(?:Opened|Failed)\s*\+=|new\s+TaskCompletionSource", body);
        Assert.DoesNotMatch(@"\b(?:_loadingImage|_imageLoad)\s*=", body);
    }

    [Fact]
    public void ImageWaitingStillPrecedesInitialNavigationAndTheExistingFade()
    {
        var path = Path.Combine(AppDataRootsTests.RepositoryRoot, "AniMeido.App", "MainWindow.xaml.cs");
        var source = File.ReadAllText(path);
        var start = source.IndexOf("private async Task LoadMainAfterMigrationCoreAsync()", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("internal void BeginShutdown()", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var startup = source[start..end];
        var wait = startup.IndexOf("await _splash.WaitForImageAsync()", StringComparison.Ordinal);
        var navigate = startup.IndexOf("_navigationService.NavigateTopLevel(", StringComparison.Ordinal);
        var fade = startup.IndexOf("await _splash.FadeOutAsync()", StringComparison.Ordinal);
        Assert.True(wait >= 0 && navigate > wait && fade > navigate);
        var fadeBody = Method("FadeOutAsync");
        Assert.Matches(@"await\s+Task\.Delay\(\s*800\s*\)[\s\S]*StartAnimation\([\s\S]*await\s+Task\.Delay\(\s*800\s*\)",
            fadeBody);
        Assert.Contains("TimeSpan.FromMilliseconds(600)", fadeBody, StringComparison.Ordinal);
        Assert.Contains("_splashOverlay.Visibility = Visibility.Collapsed", fadeBody, StringComparison.Ordinal);
    }

    private static Match NamedBinding(string body, string eventName)
    {
        var match = Regex.Match(body, @"_loadingImage\." + eventName + @"\s*\+=\s*(\w+)\s*;");
        Assert.True(match.Success, $"Register the named {eventName} handler when loading starts.");
        return match;
    }

    private static string Method(string name)
    {
        var path = Path.Combine(AppDataRootsTests.RepositoryRoot, "AniMeido.App", "Services", "SplashCoordinator.cs");
        var source = Regex.Replace(File.ReadAllText(path), @"//[^\r\n]*", string.Empty);
        var start = Regex.Match(source, @"(?m)^ {4}(?:private|public)\s+[^\r\n{;=]*\b"
            + Regex.Escape(name) + @"\s*\(");
        Assert.True(start.Success, $"Splash method not found: {name}");
        var remaining = source[(start.Index + start.Length)..];
        var next = Regex.Match(remaining, @"(?m)^ {4}(?:private|public)\s+");
        return next.Success
            ? source.Substring(start.Index, start.Length + next.Index)
            : source[start.Index..];
    }
}
