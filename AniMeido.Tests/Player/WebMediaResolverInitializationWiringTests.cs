using System.Text.RegularExpressions;

namespace AniMeido.Tests;

// 直接核对生产方法的资源归属与发布顺序，不代表原生 WebView 初始化故障已复现。
[Trait("Category", "Mechanism")]
public sealed class WebMediaResolverInitializationWiringTests
{
    [Fact]
    public void CreationOwnsTheControllerBeforeConfiguringItAndPublishesReadinessLast()
    {
        var create = CreateMethod();
        var nativeCreation = create.IndexOf("CreateCoreWebView2ControllerAsync(", StringComparison.Ordinal);
        Assert.True(nativeCreation >= 0);
        var createdLocal = Regex.Match(create,
            @"var\s+(\w+)\s*=\s*await\s+[^;]*CreateCoreWebView2ControllerAsync\s*\([^;]*\)\s*;");
        var ownership = createdLocal.Success
            ? Regex.Match(create, @"\b_controller\s*=\s*" + Regex.Escape(createdLocal.Groups[1].Value) + @"\s*;")
            : Regex.Match(create, @"\b_controller\s*=\s*await\s+[^;]*CreateCoreWebView2ControllerAsync\s*\([^;]*\)\s*;");
        Assert.True(ownership.Success && ownership.Index + ownership.Length > nativeCreation,
            "The actual native creation result must be owned before any controller configuration can fail.");
        foreach (var property in new[] { "Bounds", "IsVisible" })
        {
            var setting = Regex.Match(create, @"\." + property + @"\s*=");
            Assert.True(setting.Success && ownership.Index + ownership.Length <= setting.Index,
                "Native property setters must not precede controller ownership.");
        }

        var core = Regex.Match(create, @"\b_coreWebView\s*=\s*\w+\.CoreWebView2\s*;");
        var script = create.IndexOf("AddScriptToExecuteOnDocumentCreatedAsync(", StringComparison.Ordinal);
        var filter = Regex.Match(create, @"AddWebResourceRequestedFilter\s*\([^;]*\)\s*;");
        var ready = Regex.Matches(create, @"\b_webViewReady\s*=\s*true\s*;");
        Assert.True(core.Success && core.Index > ownership.Index);
        Assert.True(script > core.Index && filter.Success && filter.Index > script);
        Assert.Single(ready.Cast<Match>());
        Assert.True(ready[0].Index >= filter.Index + filter.Length,
            "The browser is usable only after the script await and resource filter are installed.");
        Assert.Matches(@"await\s+[^;]*AddScriptToExecuteOnDocumentCreatedAsync\s*\([^;]*\)\s*;", create);
    }

    [Fact]
    public void EnsureAndCreationUseReadinessAndCloseAnUnreadyOwnerBeforeRecreating()
    {
        var ensure = ResolverMethod("private async Task EnsureWebViewAsync(", "private async Task CreateWebViewAsync(");
        var create = CreateMethod();
        foreach (var method in new[] { ensure, create })
        {
            Assert.Matches(@"if\s*\(\s*_webViewReady\s*\)\s*\{\s*return\s*;\s*\}", method);
            Assert.DoesNotMatch(@"if\s*\(\s*_coreWebView\s+is\s+not\s+null\s*\)\s*\{\s*return\s*;", method);
        }

        var pendingOwner = Regex.Match(create,
            @"if\s*\(\s*_controller\s*(?:is\s+not\s+null|!=\s*null)\s*\)\s*\{\s*CloseHiddenController\(\s*\)\s*;\s*\}");
        var creation = create.IndexOf("CreateCoreWebView2ControllerAsync(", StringComparison.Ordinal);
        Assert.True(pendingOwner.Success && creation > pendingOwner.Index + pendingOwner.Length,
            "An unready controller must be closed successfully before its owner can be replaced.");
    }

    [Fact]
    public void InitializationFailureUsesTheExistingClosePathAndReportsBothFailures()
    {
        var create = CreateMethod();
        var catches = Regex.Matches(create, @"catch\s*\(\s*Exception\s+(\w+)\s*\)\s*\{");
        Assert.True(catches.Count >= 2,
            "Initialization and its failed cleanup must retain their original exceptions.");
        var initialization = catches[0];
        var cleanup = catches[1];
        var failurePath = create[initialization.Index..];
        var close = failurePath.IndexOf("CloseHiddenController(", StringComparison.Ordinal);
        Assert.True(close >= 0 && initialization.Index + close < cleanup.Index);
        Assert.Matches(@"new\s+AggregateException\s*\(\s*"
            + Regex.Escape(initialization.Groups[1].Value) + @"\s*,\s*"
            + Regex.Escape(cleanup.Groups[1].Value) + @"\s*\)", failurePath);
        Assert.Matches(@"throw\s*;", failurePath);
        Assert.DoesNotMatch(@"\b(?:_controller|_coreWebView)\s*=\s*null\s*;", failurePath);
        Assert.DoesNotMatch(@"\b_webViewReady\s*=\s*true\s*;", failurePath);
    }

    [Fact]
    public void SuccessfulNativeCloseClearsReadinessOnlyAfterUnhookingAndReleasingItsOwner()
    {
        var close = ResolverMethod("private void CloseHiddenController()", "private async Task<string?> ReadCookiesOnUiAsync(");
        var nativeClose = Regex.Match(close, @"\b_controller\??\.Close\(\s*\)\s*;");
        var clearHistory = Regex.Match(close, @"\b_navigationHistory\.Clear\(\s*\)\s*;");
        var clearCore = Regex.Match(close, @"\b_coreWebView\s*=\s*null\s*;");
        var clearController = Regex.Match(close, @"\b_controller\s*=\s*null\s*;");
        var clearReady = Regex.Match(close, @"\b_webViewReady\s*=\s*false\s*;");
        Assert.True(nativeClose.Success && clearCore.Success && clearController.Success && clearReady.Success);
        foreach (var eventName in new[] { "NavigationStarting", "NavigationCompleted" })
        {
            var unhook = Regex.Match(close, @"\." + eventName + @"\s*-=\s*\w+\s*;");
            Assert.True(unhook.Success && unhook.Index > nativeClose.Index
                && unhook.Index + unhook.Length < clearCore.Index,
                "Do not detach events or drop ownership before native Close succeeds.");
        }
        Assert.True(clearHistory.Success && clearHistory.Index > nativeClose.Index
            && clearHistory.Index < clearCore.Index);
        Assert.True(clearCore.Index > nativeClose.Index && clearController.Index > nativeClose.Index);
        Assert.True(clearReady.Index > clearCore.Index && clearReady.Index > clearController.Index);
    }

    private static string CreateMethod()
        => ResolverMethod("private async Task CreateWebViewAsync(", "private void OnHiddenNavigationStarting(");

    private static string ResolverMethod(string start, string next)
    {
        var path = Path.Combine(AppDataRootsTests.RepositoryRoot, "Plugins", "AniMeido.Plugin.Player",
            "Sources", "Web", "WebMediaResolver.cs");
        var source = Regex.Replace(File.ReadAllText(path), @"//[^\r\n]*", string.Empty);
        var first = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(first >= 0, $"Resolver method not found: {start}");
        var last = source.IndexOf(next, first + start.Length, StringComparison.Ordinal);
        Assert.True(last > first, $"Resolver method not found: {next}");
        return source[first..last];
    }
}
