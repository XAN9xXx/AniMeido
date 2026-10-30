namespace AniMeido.Tests;

[Trait("Category", "Mechanism")]
public sealed class PluginHostStopFailureWiringTests
{
    [Theory]
    [InlineData("StopWithGateAsync", "private Task StartDeferredStop(")]
    [InlineData("ContinueStopAfterGateAsync", "private void TrackDeferredCleanup(")]
    [InlineData("DisposeWithGateAsync", "private Task StartDeferredDispose(")]
    [InlineData("ContinueDisposeAfterGateAsync", "internal static PluginHostSnapshot CreateManifestSnapshot(")]
    public void StopPublishesItsResultBeforeTheExitObserverCanTakeTheGate(
        string method,
        string followingMethod)
    {
        var source = ReadSource("Services", "PluginHostSession.cs");
        var body = Between(source, $"private async Task {method}(", followingMethod);
        var finish = body.IndexOf("CompleteStopLifecycle();", StringComparison.Ordinal);
        var release = body.IndexOf("_gate.Release();", StringComparison.Ordinal);
        var result = body.IndexOf("completion.TrySetResult();", StringComparison.Ordinal);
        var failure = body.IndexOf("completion.TrySetException(failure);", StringComparison.Ordinal);

        Assert.True(finish >= 0 && result >= 0 && failure >= 0);
        Assert.True(release > finish && release > result && release > failure);
    }

    [Fact]
    public void ShutdownReportsCleanupFailureAsErrorAndStillClosesTheWindow()
    {
        var source = ReadSource("App.xaml.cs");
        var closing = Between(
            source,
            "private async void OnMainAppWindowClosing(",
            "private async Task StopApplicationServicesAsync(");

        var report = closing.IndexOf(
            "Log.Error(ex, \"应用关闭清理失败\")", StringComparison.Ordinal);
        var finish = closing.IndexOf("finally", StringComparison.Ordinal);
        var close = closing.IndexOf("_window?.Close();", StringComparison.Ordinal);

        Assert.True(report >= 0 && finish > report && close > finish);
        Assert.Contains("Log.CloseAndFlush();", closing[finish..], StringComparison.Ordinal);
    }

    [Fact]
    public void DeferredCleanupReportsItsOwnFailureAtTheFinalErrorBoundary()
    {
        var source = ReadSource("Services", "PluginHostSession.cs");
        var observe = Between(
            source,
            "private async Task ObserveDeferredCleanupAsync(",
            "private void CompleteStopLifecycle(");

        var wait = observe.IndexOf("await deferred;", StringComparison.Ordinal);
        var report = observe.IndexOf("_logger.LogError(", StringComparison.Ordinal);
        Assert.True(wait >= 0 && report > wait);
        Assert.Contains("Deferred PluginHost {PluginId} cleanup faulted.", observe,
            StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] relative)
        => File.ReadAllText(Path.Combine(
            new[] { AppDataRootsTests.RepositoryRoot, "AniMeido.App" }
                .Concat(relative).ToArray()));

    private static string Between(string source, string start, string end)
    {
        var first = source.IndexOf(start, StringComparison.Ordinal);
        var last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        Assert.True(first >= 0 && last > first);
        return source[first..last];
    }
}
