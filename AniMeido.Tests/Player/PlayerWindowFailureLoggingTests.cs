namespace AniMeido.Tests;

[Trait("Category", "Mechanism")]
public sealed class PlayerWindowFailureLoggingTests
{
    [Fact]
    public void SourceRefreshLogsItsOriginalFailureAndKeepsTheReloadResultMessage()
    {
        var body = Between(ReadSource(),
            "internal async Task RefreshSourcesAsync()",
            "private void TrackDpiChanges()");

        AssertLogsOriginalFailure(body);
        Assert.Contains("播放源已更新，但重新查询失败：{ex.Message}", body, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticExportLogsItsOriginalFailureAndKeepsTheDialogMessage()
    {
        var diagnostics = Between(ReadSource(),
            "private async void OnSourceDiagnosticsClick(",
            "private string GetDiagnosticStatusText()");
        var export = Between(diagnostics,
            "export.Click += async (_, _) =>",
            "var clear = new Button");

        AssertLogsOriginalFailure(export);
        Assert.Contains("if (file is null)", export, StringComparison.Ordinal);
        Assert.Contains("导出失败：{ex.Message}", export, StringComparison.Ordinal);
    }

    [Fact]
    public void PlayerCommandLogsItsOriginalFailureAndKeepsTheControlMessage()
    {
        var body = Between(ReadSource(),
            "private void TryRunPlayerCommand(",
            "private void OnVideoSurfaceSizeChanged(");

        AssertLogsOriginalFailure(body);
        Assert.Contains("播放控制失败：{ex.Message}", body, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstActivationLogsItsOriginalFailureAndStillStopsTheNativePlayer()
    {
        var body = Between(ReadSource(),
            "private async void OnWindowActivated(",
            "private void InitializeNativePlayer()");

        AssertLogsOriginalFailure(body);
        Assert.Contains("播放器初始化失败：{ex.Message}", body, StringComparison.Ordinal);
        Assert.Contains("await StopNativePlayerAsync();", body, StringComparison.Ordinal);
    }

    [Fact]
    public void NewResolutionCancelsThePreviousRequestBeforePublishingItsLoadingState()
    {
        // 仅核对 WinUI 接线顺序；真实解析期间的加载反馈由窗口验收确认。
        var body = Between(ReadSource(),
            "private async Task StartSelectedEpisodeCoreAsync(",
            "private Uri? _lastTimedOutPageUri;");
        var cancel = body.IndexOf("CancelPlaybackResolution();", StringComparison.Ordinal);
        var loading = body.IndexOf("_loading.IsActive = true;", StringComparison.Ordinal);
        var resolving = body.IndexOf("PlaybackViewState.Resolving", StringComparison.Ordinal);
        var resolve = body.IndexOf("await _sourceCatalog.ResolveAsync(", StringComparison.Ordinal);

        Assert.True(cancel >= 0 && loading > cancel && resolving > cancel && resolve > loading);
    }

    private static void AssertLogsOriginalFailure(string body)
    {
        var caught = body.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
        Assert.True(caught >= 0);
        Assert.Matches(@"_logger\.LogError\(\s*ex\s*,", body[caught..]);
    }

    private static string ReadSource()
        => File.ReadAllText(Path.Combine(AppDataRootsTests.RepositoryRoot,
            "Plugins", "AniMeido.Plugin.Player", "Views", "PlayerWindow.cs"));

    private static string Between(string source, string declaration, string followingDeclaration)
    {
        var first = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(first >= 0, $"Declaration not found: {declaration}");
        var last = source.IndexOf(followingDeclaration, first + declaration.Length, StringComparison.Ordinal);
        Assert.True(last > first, $"Following declaration not found: {followingDeclaration}");
        return source[first..last];
    }
}
