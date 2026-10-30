using System.Text.RegularExpressions;

namespace AniMeido.Tests;

[Trait("Category", "Mechanism")]
public sealed class LegacyInstallerGuardTests
{
    [Fact]
    public void UnknownLegacyDirectoryMarksDetectionFailedBeforeRegisteringAnUninstaller()
    {
        var read = Section("procedure ReadLegacy(", "procedure DetectLegacy()");
        var guard = Match(read, """
            if\s+not\s+RegQueryStringValue\(Root,\s*LegacyKey,\s*'InstallLocation',\s*Directory\)
            \s+or\s+\(Trim\(Directory\)\s*=\s*''\)\s+then\s+begin(?<body>.*?)end;
            """);

        Assert.True(guard.Success, "Missing or blank legacy directories must stop detection before registration.");
        Assert.Contains("LegacyDetectionFailed := True;", guard.Groups["body"].Value, StringComparison.Ordinal);
        Assert.Contains("Exit;", guard.Groups["body"].Value, StringComparison.Ordinal);
        Assert.True(guard.Index + guard.Length < read.IndexOf("SetArrayLength", StringComparison.Ordinal));
        Assert.True(read.IndexOf("if not RegKeyExists(Root, LegacyKey) then Exit;", StringComparison.Ordinal) < guard.Index);
        Assert.Contains("LegacyDirectories[N] := Directory;", read, StringComparison.Ordinal);
        Assert.Contains("LegacyCommands[N] := Command;", read, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedLegacyDetectionReturnsItsOwnRecoveryMessageBeforeInstallationChecks()
    {
        var prepare = Section("function PrepareToInstall(", "procedure RegisterExtraCloseApplicationsResources()");
        var guard = Match(prepare, @"if\s+LegacyDetectionFailed\s+then\s+begin(?<body>.*?)end;");

        Assert.True(guard.Success);
        Assert.Contains("Result := CustomMessage('LegacyDetectionFailed');", guard.Groups["body"].Value,
            StringComparison.Ordinal);
        Assert.Contains("Exit;", guard.Groups["body"].Value, StringComparison.Ordinal);
        Assert.True(guard.Index < prepare.IndexOf("for I :=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("english", "uninstall", "try again")]
    [InlineData("chinesesimplified", "卸载旧版", "重试")]
    public void RecoveryMessageTellsTheUserToUninstallTheOldVersionAndRetry(
        string language, string uninstall, string retry)
    {
        var messages = Section("[CustomMessages]", "[Tasks]");
        var prefix = language + ".LegacyDetectionFailed=";
        var line = Assert.Single(messages.Split('\n'), value => value.StartsWith(prefix, StringComparison.Ordinal));

        Assert.Contains(uninstall, line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(retry, line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFailureFromTheFirstRegistryRootIsNotResetBeforeReadingTheSecond()
    {
        var detect = Section("procedure DetectLegacy()", "function InDirectory(");
        var first = detect.IndexOf("ReadLegacy(HKLM64);", StringComparison.Ordinal);
        var second = detect.IndexOf("ReadLegacy(HKCU64);", StringComparison.Ordinal);

        Assert.True(first >= 0 && second > first);
        Assert.Contains("LegacyDetectionFailed := False;", detect[..first], StringComparison.Ordinal);
        Assert.DoesNotContain("LegacyDetectionFailed := False;", detect[first..second], StringComparison.Ordinal);
        Assert.Contains("LegacyDetectionFailed := True;", detect[second..], StringComparison.Ordinal);
    }

    private static string Section(string start, string end)
    {
        var script = File.ReadAllText(Path.Combine(AppDataRootsTests.RepositoryRoot, "Installer", "AniMeido.iss"));
        var first = script.IndexOf(start, StringComparison.Ordinal);
        Assert.True(first >= 0, $"Installer section not found: {start}");
        var last = script.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        Assert.True(last > first, $"Installer section not found: {end}");
        return script[first..last];
    }

    private static Match Match(string source, string pattern)
        => Regex.Match(source, pattern,
            RegexOptions.Singleline | RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
}
