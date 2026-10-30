using System.Text.RegularExpressions;

namespace AniMeido.Tests;

[Trait("Category", "Mechanism")]
public sealed class TrayIconRecoveryWiringTests
{
    [Fact]
    public void BroadcastRecoveryIsAllowedForTheTopLevelWindowBeforeTheSessionIsReady()
    {
        var source = ReadSource("Services", "TrayIconService.cs");
        Assert.Matches(@"_taskbarCreatedMessage\s*=\s*RegisterWindowMessage\(\s*""TaskbarCreated""\s*\)", source);
        var run = MethodBody(source, "RunCore");
        var create = Regex.Match(run, @"_window\s*=\s*CreateWindowEx\((?<arguments>.*?)\)\s*;", RegexOptions.Singleline);
        Assert.True(create.Success);
        var arguments = create.Groups["arguments"].Value.Split(',').Select(argument => argument.Trim()).ToArray();
        Assert.Equal(12, arguments.Length);
        Assert.Equal("0", arguments[8]);

        var filter = Regex.Match(run,
            @"ChangeWindowMessageFilterEx\(\s*_window\s*,\s*_taskbarCreatedMessage\s*,\s*MsgfltAllow\s*,\s*0\s*\)");
        var ready = Regex.Match(run, @"\bsession\.MarkReady\(\s*\)");
        Assert.True(filter.Success && filter.Index > create.Index);
        Assert.True(ready.Success && ready.Index > filter.Index);
    }

    [Fact]
    public void BroadcastForTheRunningWindowReaddsTheIconAndContainsRecoveryFailures()
    {
        var source = ReadSource("Services", "TrayIconService.cs");
        var window = MethodBody(source, "WindowProc");
        var recovery = Regex.Match(window, @"HandleTaskbarCreated\(\s*session\s*\)");
        Assert.True(recovery.Success);
        var route = window[..recovery.Index];
        Assert.Contains("_taskbarCreatedMessage", route, StringComparison.Ordinal);
        Assert.Matches(@"(?:window\s*==\s*_window|_window\s*==\s*window)", route);
        Assert.Contains("session.IsRunning", route, StringComparison.Ordinal);

        var handle = MethodBody(source, "HandleTaskbarCreated");
        Assert.Matches(@"!\s*ShellNotifyIcon\(\s*NimAdd\s*,\s*ref\s+\w+\s*\)", handle);
        var caught = handle.IndexOf("catch", StringComparison.Ordinal);
        var failed = handle.IndexOf("session.RecordFailure(", StringComparison.Ordinal);
        var quit = handle.IndexOf("PostQuitMessage(", StringComparison.Ordinal);
        Assert.True(caught >= 0 && failed > caught && quit > failed);
        Assert.DoesNotMatch(@"\bthrow\b", handle[caught..]);
    }

    [Fact]
    public void QueuedCallbacksKeepTheirSessionAndFailureNotificationWaitsForCleanup()
    {
        var source = ReadSource("Services", "TrayIconService.cs");
        Assert.Matches(@"\bTrayIconSession\??\s+_session\b", source);
        var enqueue = MethodBody(source, "Enqueue");
        var queue = enqueue.IndexOf("TryEnqueue(", StringComparison.Ordinal);
        var guards = Regex.Matches(enqueue, @"IsCurrentSession\(\s*session\s*\)");
        Assert.True(queue >= 0 && guards.Count >= 2);
        Assert.True(guards[0].Index < queue && guards[1].Index > queue);
        Assert.Matches(@"\baction\s*\(\s*\)", enqueue[guards[1].Index..]);
        Assert.DoesNotContain("session.IsRunning", enqueue, StringComparison.Ordinal);
        var actions = MethodBody(source, "WindowProc") + MethodBody(source, "ShowMenu");
        Assert.Contains("session.InvokeOpen", actions, StringComparison.Ordinal);
        Assert.Contains("session.InvokeExit", actions, StringComparison.Ordinal);

        var run = MethodBody(source, "Run");
        var cleanup = run.IndexOf("finally", StringComparison.Ordinal);
        var clearThread = Regex.Match(run, @"_thread\s*=\s*null\s*;");
        var complete = run.IndexOf("session.Complete(", StringComparison.Ordinal);
        var notify = Regex.Match(run, @"Enqueue\(\s*session\s*,\s*session\.NotifyUnavailable\s*\)");
        Assert.True(cleanup >= 0 && clearThread.Success && clearThread.Index > cleanup);
        Assert.True(complete > clearThread.Index && notify.Success && notify.Index > complete);
    }

    [Fact]
    public void RuntimeFailureRestoresTheWindowWithoutSavingAndLateSettingSavesCheckTheRunningSession()
    {
        var source = ReadSource("App.xaml.cs");
        var unavailable = MethodBody(source, "OnTrayUnavailable");
        var disable = Regex.Match(unavailable, @"KeepInTrayOnClose\s*=\s*false");
        var activate = unavailable.IndexOf("ActivateMainWindow(", StringComparison.Ordinal);
        Assert.True(disable.Success && activate > disable.Index);
        var guard = unavailable[..disable.Index];
        Assert.Contains("_shutdownStarted", guard, StringComparison.Ordinal);
        Assert.Contains("_exitRequested", guard, StringComparison.Ordinal);
        Assert.Matches(@"\breturn\s*;", guard);
        Assert.DoesNotContain("UpdateAsync(", unavailable, StringComparison.Ordinal);
        Assert.DoesNotContain("StartTrayIcon", unavailable, StringComparison.Ordinal);

        var start = source.IndexOf("private Task StartTrayIconAsync(", StringComparison.Ordinal);
        var recovery = source.IndexOf("private void OnTrayUnavailable(", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && recovery > start);
        Assert.Matches(@"\.StartAsync\([^;]*RequestExit\s*,\s*OnTrayUnavailable\s*\)", source[start..recovery]);

        var startup = MethodBody(source, "StartTrayIconAtStartupAsync");
        Assert.Matches(@"\.UpdateAsync\(\s*settings\s*=>\s*settings\s+with\s*\{\s*KeepInTrayOnClose\s*=\s*false\s*\}\s*\)", startup);

        var closing = MethodBody(source, "OnMainAppWindowClosing");
        var hide = closing.IndexOf("HideMainWindow(", StringComparison.Ordinal);
        Assert.True(hide >= 0);
        Assert.Contains("TrayIconService", closing[..hide], StringComparison.Ordinal);
        Assert.Contains("IsRunning", closing[..hide], StringComparison.Ordinal);

        var setting = MethodBody(source, "SetKeepInTrayOnCloseAsync");
        var saved = Regex.Match(setting, @"var\s+(?<saved>\w+)\s*=\s*await\b[^;]*?\.UpdateAsync\(", RegexOptions.Singleline);
        Assert.True(saved.Success);
        var savedName = Regex.Escape(saved.Groups["saved"].Value);
        Assert.Matches(@"_desktopSettings\s*=\s*" + savedName + @"\s+with\s*\{\s*KeepInTrayOnClose\s*=\s*"
            + savedName + @"\.KeepInTrayOnClose\s*&&\s*trayIcon\.IsRunning\s*,?\s*\}", setting);
    }

    private static string ReadSource(params string[] relative)
    {
        var path = Path.Combine(new[] { AppDataRootsTests.RepositoryRoot, "AniMeido.App" }.Concat(relative).ToArray());
        return Regex.Replace(File.ReadAllText(path), @"//[^\r\n]*|/\*.*?\*/", string.Empty, RegexOptions.Singleline);
    }

    private static string MethodBody(string source, string name)
    {
        var method = Regex.Match(source,
            @"\b(?:private|internal|public)\s+(?:(?:async|static)\s+)*[\w<>?]+\s+" + Regex.Escape(name)
            + @"\s*\([^)]*\)\s*\{", RegexOptions.Singleline);
        Assert.True(method.Success, $"Method not found: {name}");
        var start = method.Index + method.Length;
        var depth = 1;
        for (var index = start; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' && --depth == 0)
                return source[start..index];
        }
        throw new InvalidOperationException($"Method has no closing brace: {name}");
    }
}
