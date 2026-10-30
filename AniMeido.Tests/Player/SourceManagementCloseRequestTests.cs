using System.Text.RegularExpressions;

namespace AniMeido.Tests;

// 原生窗口事件只检查请求路由；共享解析器上的取消与真实排空由组合行为测试覆盖。
[Trait("Category", "Mechanism")]
public sealed class SourceManagementCloseRequestTests
{
    [Fact]
    public void TitleBarCloseRequestsTheOwningSessionBeforeWaitingForWindowCleanup()
    {
        var window = ReadSource("Views", "SourceManagementWindow.cs");
        var handler = window[window.IndexOf("private async void OnWindowClosing(", StringComparison.Ordinal)..];
        handler = handler[..handler.IndexOf("public Task CloseAsync()", StringComparison.Ordinal)];
        Assert.Contains("args.Cancel = true", handler, StringComparison.Ordinal);
        Assert.Contains("await _requestSessionClose()", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("await CloseAsync()", handler, StringComparison.Ordinal);

        Assert.Matches(@"Func<Task>\s+requestSessionClose", window);
        Assert.Contains("_requestSessionClose = requestSessionClose", window, StringComparison.Ordinal);
        var manager = ReadSource("Services", "PlayerWindowManager.cs");
        var construction = Regex.Match(manager, @"new SourceManagementWindow\([\s\S]*?\);");
        Assert.True(construction.Success);
        Assert.Contains("EndSessionFromWindowAsync", construction.Value, StringComparison.Ordinal);
    }

    private static string ReadSource(string directory, string name)
        => File.ReadAllText(Path.Combine(AppDataRootsTests.RepositoryRoot, "Plugins",
            "AniMeido.Plugin.Player", directory, name));
}
