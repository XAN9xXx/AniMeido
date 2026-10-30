using System.Text.RegularExpressions;

namespace AniMeido.Tests;

// 检查真实页面的备份结果与提示接线；备份数据行为由现有服务回归覆盖。
[Trait("Category", "Mechanism")]
public sealed class DataManagementBackupResultTests
{
    [Fact]
    public void BackupLoggingUsesTheExistingRequiredPageConstructorAndApplicationLogger()
    {
        var source = PageSource();
        Assert.Matches(@"private\s+readonly\s+ILogger<DataManagementSettingsPage>\s+_logger\s*;", source);
        var constructor = PageMethod("DataManagementSettingsPage");
        Assert.Matches(@"ILogger<DataManagementSettingsPage>\s+logger\s*\)", constructor);
        Assert.Matches(@"_logger\s*=\s*logger\s*;", constructor);

        var root = AppDataRootsTests.RepositoryRoot;
        var factory = File.ReadAllText(Path.Combine(root, "AniMeido.App", "Services", "PageFactory.cs"));
        var registration = File.ReadAllText(Path.Combine(root, "AniMeido.App", "Services", "ServiceRegistration.cs"));
        Assert.Contains("ActivatorUtilities.CreateInstance(_serviceProvider, pageType)", factory, StringComparison.Ordinal);
        Assert.Contains("services.AddLogging(", registration, StringComparison.Ordinal);
    }

    [Fact]
    public void BackupAwaitAndSuccessMessageHaveSeparateFailureScopes()
    {
        var body = PageMethod("OnBackupNowClick");
        var backup = Regex.Match(body, @"try\s*\{\s*await\s+_transfer\.BackupAsync\(\s*\)\s*;\s*\}");
        Assert.True(backup.Success, "The operation's try body contains only the actual backup await.");
        var success = SuccessMessageScope(body);
        Assert.True(success.Index > backup.Index + backup.Length);
        var failures = Regex.Matches(body, @"catch\s*\(\s*Exception\s+(\w+)\s*\)");
        Assert.Equal(2, failures.Count);
        Assert.True(failures[0].Index > backup.Index && failures[0].Index < success.Index);
        Assert.True(failures[1].Index > success.Index + success.Length);
        Assert.Single(Regex.Matches(body, @"\b_transfer\.BackupAsync\("));
    }

    [Fact]
    public void BusyBackupReportsTemporaryUnavailabilityWithoutAnOperationError()
    {
        var body = PageMethod("OnBackupNowClick");
        var busy = Regex.Match(body, @"catch\s*\(\s*PersonalDataBusyException\s+(\w+)\s*\)");
        Assert.True(busy.Success);
        var failure = Regex.Match(body[(busy.Index + busy.Length)..], @"catch\s*\(\s*Exception\s+\w+\s*\)");
        Assert.True(failure.Success);
        var branch = body.Substring(busy.Index, busy.Length + failure.Index);
        Assert.Contains("\"暂时无法备份\"", branch, StringComparison.Ordinal);
        Assert.Contains(busy.Groups[1].Value + ".Message", branch, StringComparison.Ordinal);
        Assert.Contains("return;", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("LogError(", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("\"备份失败\"", branch, StringComparison.Ordinal);
    }

    [Fact]
    public void BackupFailureLogsTheOriginalExceptionOnceBeforeItsFailureMessage()
    {
        var body = PageMethod("OnBackupNowClick");
        var failure = Regex.Match(body, @"catch\s*\(\s*Exception\s+(\w+)\s*\)");
        var success = SuccessMessageScope(body);
        Assert.True(failure.Success && success.Index > failure.Index);
        var branch = body[failure.Index..success.Index];
        var log = Regex.Match(branch, @"_logger\.LogError\(\s*" + Regex.Escape(failure.Groups[1].Value) + @"\s*,");
        var message = branch.IndexOf("await ShowMessageAsync(", StringComparison.Ordinal);
        Assert.True(log.Success && message > log.Index);
        Assert.Single(Regex.Matches(branch, @"\b_logger\.LogError\("));
        Assert.Contains("\"备份失败\"", branch, StringComparison.Ordinal);
        Assert.Contains("return;", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("LogWarning(", branch, StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessMessageFailureOnlyWarnsAndNeverReportsOrRepeatsABackupFailure()
    {
        var body = PageMethod("OnBackupNowClick");
        var success = SuccessMessageScope(body);
        var remaining = body[(success.Index + success.Length)..];
        var failure = Regex.Match(remaining, @"catch\s*\(\s*Exception\s+(\w+)\s*\)");
        var cleanup = remaining.IndexOf("finally", StringComparison.Ordinal);
        Assert.True(failure.Success && cleanup > failure.Index);
        var branch = remaining[failure.Index..cleanup];
        Assert.Matches(@"_logger\.LogWarning\(\s*" + Regex.Escape(failure.Groups[1].Value) + @"\s*,", branch);
        Assert.Single(Regex.Matches(branch, @"\b_logger\.LogWarning\("));
        Assert.DoesNotContain("LogError(", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowMessageAsync(", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("\"备份失败\"", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("_transfer.", branch, StringComparison.Ordinal);
    }

    [Fact]
    public void BackupKeepsItsOriginalVisitAndRestoresTheButtonAfterBothStages()
    {
        var body = PageMethod("OnBackupNowClick");
        var captured = Regex.Match(body, @"(?:var|int)\s+(\w+)\s*=\s*_visitVersion\s*;");
        Assert.True(captured.Success);
        var visit = Regex.Escape(captured.Groups[1].Value);
        var writing = body.IndexOf("await _transfer.BackupAsync()", StringComparison.Ordinal);
        Assert.True(writing > captured.Index);
        Assert.Matches(@"if\s*\(\s*!IsCurrentVisit\(\s*" + visit + @"\s*\)\s*\)\s*(?:\{\s*)?return\s*;", body[..writing]);
        var messages = Regex.Matches(body, @"await\s+ShowMessageAsync\([\s\S]*?\)\s*;");
        Assert.Equal(3, messages.Count);
        foreach (Match message in messages)
            Assert.Matches(@",\s*" + visit + @"\s*\)\s*;\s*$", message.Value);
        var cleanup = body.LastIndexOf("finally", StringComparison.Ordinal);
        var success = SuccessMessageScope(body);
        Assert.True(cleanup > success.Index + success.Length);
        Assert.Matches(@"finally\s*\{\s*BackupNowButton\.IsEnabled\s*=\s*true\s*;\s*BackupNowButton\.Content\s*=\s*""备份""\s*;\s*\}", body[cleanup..]);
        Assert.DoesNotMatch(@"CancellationToken|\.Cancel\(|RestoreAsync\(", body);
    }

    private static Match SuccessMessageScope(string body)
    {
        var success = Regex.Match(body, @"try\s*\{\s*await\s+ShowMessageAsync\(\s*""备份完成""\s*,[\s\S]*?\)\s*;\s*\}");
        Assert.True(success.Success, "Show the success result in a separate try after the backup has completed.");
        return success;
    }

    private static string PageMethod(string name)
    {
        var source = PageSource();
        var start = Regex.Match(source, @"(?m)^ {8}(?:private|public)\s+[^\r\n{;=]*\b" + Regex.Escape(name) + @"\s*\(");
        Assert.True(start.Success, $"Data management page method not found: {name}");
        var remaining = source[(start.Index + start.Length)..];
        var next = Regex.Match(remaining, @"(?m)^ {8}(?:private|public)\s+");
        return next.Success ? source.Substring(start.Index, start.Length + next.Index) : source[start.Index..];
    }

    private static string PageSource()
    {
        var path = Path.Combine(AppDataRootsTests.RepositoryRoot, "Plugins", "AniMeido.Plugin.Base",
            "Views", "DataManagementSettingsPage.xaml.cs");
        return Regex.Replace(File.ReadAllText(path), @"//[^\r\n]*", string.Empty);
    }
}
