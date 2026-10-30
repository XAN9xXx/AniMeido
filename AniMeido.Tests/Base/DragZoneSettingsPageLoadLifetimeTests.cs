using System.Text.RegularExpressions;

namespace AniMeido.Tests;

// 检查真实页面的读取和重建接线，不代替原生下拉交互验收。
[Trait("Category", "Mechanism")]
public sealed class DragZoneSettingsPageLoadLifetimeTests
{
    [Fact]
    public void PageReceivesTheExistingLoggerFromItsConstructor()
    {
        var body = PageMethod("public DragZoneSettingsPage(", "private async Task LoadAsync()");
        Assert.Contains("ILogger<DragZoneSettingsPage> logger", body, StringComparison.Ordinal);
        Assert.Contains("_logger = logger", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("private async Task LoadAsync()", "private void OnPreviewBorderSizeChanged(", "Warning")]
    [InlineData("private async Task QueueSaveAsync()", "private static DragZoneConfig CloneConfig(", "Error")]
    public void SettingsFailureLogsItsOriginalExceptionAtTheResultBoundary(string start, string next, string level)
    {
        var body = PageMethod(start, next);
        var boundary = body.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
        Assert.True(boundary >= 0);
        var failure = body[boundary..];
        Assert.Matches(@"_logger\.Log" + level + @"\(\s*ex\s*,", failure);
        Assert.Single(Regex.Matches(failure, @"_logger\.Log(?:Warning|Error)\("));
        Assert.DoesNotContain("Debug.WriteLine", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadingDoesNotSuppressEditsAndAnObsoleteReadReturnsBeforeSuppression()
    {
        var body = PageMethod("private async Task LoadAsync()", "private void OnPreviewBorderSizeChanged(");
        var captured = Regex.Match(body, @"var\s+(\w+)\s*=\s*_changeVersion\s*;");
        var read = Regex.Match(body, @"await\s+_featureState\.LoadDragZoneConfigAsync\(\s*\)");
        var suppress = Regex.Match(body, @"_suppressEvents\s*=\s*true\s*;");
        Assert.True(captured.Success && read.Success && suppress.Success);
        Assert.True(read.Index > captured.Index && suppress.Index > read.Index + read.Length,
            "The user may edit the default zones while the stored configuration is being read.");
        var version = Regex.Escape(captured.Groups[1].Value);
        Assert.Matches(@"if\s*\(\s*(?:" + version + @"\s*!=\s*_changeVersion|_changeVersion\s*!=\s*"
            + version + @")\s*\)\s*(?:\{\s*)?return\s*;",
            body[(read.Index + read.Length)..suppress.Index]);
    }

    [Fact]
    public void SynchronousRebuildAlwaysReturnsItsEventSuppression()
    {
        var body = PageMethod("private async Task LoadAsync()", "private void OnPreviewBorderSizeChanged(");
        var scope = Regex.Match(body,
            @"(?:_suppressEvents\s*=\s*true\s*;\s*try\s*\{|try\s*\{\s*_suppressEvents\s*=\s*true\s*;)");
        var rebuild = body.IndexOf("RebuildAll()", StringComparison.Ordinal);
        var cleanup = Regex.Match(body, @"finally\s*\{\s*_suppressEvents\s*=\s*false\s*;\s*\}");
        Assert.True(scope.Success && rebuild > scope.Index && cleanup.Success && cleanup.Index > rebuild,
            "Rebuilding controls owns the suppression and releases it even when rebuilding throws.");
        Assert.DoesNotMatch(@"\bawait\b", body[scope.Index..cleanup.Index]);
        Assert.DoesNotMatch(@"\breturn\s*;", body[scope.Index..rebuild]);
    }

    [Fact]
    public void SavesStillCaptureTheLatestVersionAndSnapshotBeforeWaitingForTheirGate()
    {
        var body = PageMethod("private async Task QueueSaveAsync()", "private static DragZoneConfig CloneConfig(");
        var change = body.IndexOf("Interlocked.Increment(ref _changeVersion)", StringComparison.Ordinal);
        var version = Regex.Match(body, @"var\s+(\w+)\s*=\s*Interlocked\.Increment\(\s*ref\s+_saveVersion\s*\)\s*;");
        var snapshot = Regex.Match(body, @"var\s+(\w+)\s*=\s*_dragZones\.Select\(\s*CloneConfig\s*\)\.ToList\(\s*\)\s*;");
        var wait = body.IndexOf("await _saveGate.WaitAsync()", StringComparison.Ordinal);
        Assert.True(change >= 0 && version.Success && version.Index > change
            && snapshot.Success && snapshot.Index > version.Index && wait > snapshot.Index);
        var save = Regex.Match(body, @"await\s+_featureState\.SaveDragZoneConfigAsync\(\s*"
            + Regex.Escape(snapshot.Groups[1].Value) + @"\s*\)");
        Assert.True(save.Success && save.Index > wait);
        var capturedVersion = Regex.Escape(version.Groups[1].Value);
        Assert.Matches(@"if\s*\(\s*(?:" + capturedVersion + @"\s*!=\s*_saveVersion|_saveVersion\s*!=\s*"
            + capturedVersion + @")\s*\)\s*(?:\{\s*)?return\s*;", body[wait..save.Index]);
        Assert.Matches(@"finally\s*\{\s*_saveGate\.Release\(\s*\)\s*;\s*\}", body[save.Index..]);
    }

    private static string PageMethod(string start, string next)
    {
        var path = Path.Combine(AppDataRootsTests.RepositoryRoot, "Plugins", "AniMeido.Plugin.Base",
            "Views", "DragZoneSettingsPage.xaml.cs");
        var source = Regex.Replace(File.ReadAllText(path), @"//[^\r\n]*", string.Empty);
        var first = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(first >= 0, $"Drag-zone page method not found: {start}");
        var last = source.IndexOf(next, first + start.Length, StringComparison.Ordinal);
        Assert.True(last > first, $"Drag-zone page method not found: {next}");
        return source[first..last];
    }
}
