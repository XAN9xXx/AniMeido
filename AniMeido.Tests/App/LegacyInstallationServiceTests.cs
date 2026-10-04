using System.ComponentModel;
using System.Diagnostics;
using AniMeido.App.Services;
using Microsoft.Win32;

namespace AniMeido.Tests;

public sealed class LegacyInstallationServiceTests
{
    [Theory]
    [InlineData(RegistryHive.LocalMachine)]
    [InlineData(RegistryHive.CurrentUser)]
    public void DetectsEitherLegacyHiveAndPreservesUninstallArguments(RegistryHive hive)
    {
        using var paths = new MockAppDataPaths();
        var registry = new FakeInstallationRegistry();
        registry.Entries[hive] = new UninstallEntry(@"D:\Test-only old AniMeido",
            "\"D:\\Test-only old AniMeido\\unins000.exe\" /LOG=\"D:\\test log.txt\"");
        var result = new LegacyInstallationService(CreateContext(paths, true), registry, NeverRun).Detect();
        var installation = Assert.Single(result);
        Assert.Equal(hive, installation.Hive);
        Assert.Equal(@"D:\Test-only old AniMeido", installation.InstallLocation);
        Assert.Equal(@"D:\Test-only old AniMeido\unins000.exe", installation.Uninstaller);
        Assert.Equal("/LOG=\"D:\\test log.txt\"", installation.Arguments);
        Assert.Equal(new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser }, registry.Reads);
    }

    [Fact]
    public void BothLegacyEntriesAreRetainedAndAbsenceIsNotAnError()
    {
        using var paths = new MockAppDataPaths();
        var registry = new FakeInstallationRegistry();
        var service = new LegacyInstallationService(CreateContext(paths, true), registry, NeverRun);
        Assert.Empty(service.Detect());
        registry.Entries[RegistryHive.LocalMachine] = Entry;
        registry.Entries[RegistryHive.CurrentUser] = Entry;
        Assert.Equal(2, service.Detect().Count);
    }

    [Fact]
    public async Task NonInstalledModeDoesNotReadRegistryOrRunUninstaller()
    {
        using var paths = new MockAppDataPaths();
        var registry = new FakeInstallationRegistry();
        var service = new LegacyInstallationService(CreateContext(paths, false), registry, NeverRun);
        Assert.Empty(service.Detect());
        Assert.Empty(registry.Reads);
        Assert.False(await service.UninstallAsync(new(RegistryHive.LocalMachine, "", @"D:\test-only.exe", "")));
    }

    [Theory]
    [InlineData(RegistryHive.LocalMachine)]
    [InlineData(RegistryHive.CurrentUser)]
    public void ReadFailureDoesNotReturnPartialDetection(RegistryHive failureHive)
    {
        using var paths = new MockAppDataPaths();
        var registry = new FakeInstallationRegistry { FailureHive = failureHive };
        registry.Entries[RegistryHive.LocalMachine] = Entry;
        Assert.Empty(new LegacyInstallationService(CreateContext(paths, true), registry, NeverRun).Detect());
    }

    [Theory]
    [InlineData("")]
    [InlineData("unins000.exe")]
    [InlineData("\"D:\\test-only\\unins000.exe")]
    [InlineData("\"D:\\test-only\\not-exe.txt\"")]
    public void MalformedLegacyCommandDoesNotDisplayNotice(string command)
    {
        using var paths = new MockAppDataPaths();
        var registry = new FakeInstallationRegistry();
        registry.Entries[RegistryHive.LocalMachine] = new(@"D:\test-only", command);
        Assert.Empty(new LegacyInstallationService(CreateContext(paths, true), registry, NeverRun).Detect());
    }

    [Theory]
    [InlineData("\"D:\\old app\\unins000.exe\" /SILENT", @"D:\old app\unins000.exe", "/SILENT")]
    [InlineData("D:\\old app\\unins000.exe /SILENT", @"D:\old app\unins000.exe", "/SILENT")]
    [InlineData("\"D:\\old app\\unins000.exe\"", @"D:\old app\unins000.exe", "")]
    public void SplitsCommandWithoutUsingAShellInterpreter(string command, string file, string arguments)
    {
        Assert.Equal((file, arguments), LegacyInstallationService.SplitUninstallString(command));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UninstallUsesShellElevationAndReportsCompletion(bool completes)
    {
        using var paths = new MockAppDataPaths();
        ProcessStartInfo? captured = null;
        var service = new LegacyInstallationService(CreateContext(paths, true), new FakeInstallationRegistry(),
            start => { captured = start; return Task.FromResult(completes); });
        Assert.Equal(completes, await service.UninstallAsync(new(RegistryHive.LocalMachine,
            @"D:\test-only", @"D:\test-only\unins000.exe", "/LOG")));
        Assert.NotNull(captured);
        Assert.True(captured.UseShellExecute);
        Assert.Equal("runas", captured.Verb);
        Assert.Equal(@"D:\test-only\unins000.exe", captured.FileName);
        Assert.Equal("/LOG", captured.Arguments);
    }

    [Fact]
    public async Task UacCancellationDoesNotEscapeToTheUi()
    {
        using var paths = new MockAppDataPaths();
        var service = new LegacyInstallationService(CreateContext(paths, true), new FakeInstallationRegistry(),
            _ => throw new Win32Exception(1223));
        Assert.False(await service.UninstallAsync(new(RegistryHive.LocalMachine, "", @"D:\test-only.exe", "")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RestoresOnlyRemovedShortcutsWithoutPersistentBackup(bool completes)
    {
        using var paths = new MockAppDataPaths();
        var removed = Path.Combine(paths.RootDirectory, "new-menu.lnk");
        var modified = Path.Combine(paths.RootDirectory, "new-desktop.lnk");
        var absent = Path.Combine(paths.RootDirectory, "not-selected.lnk");
        File.WriteAllBytes(removed, [0, 1, 2, 3]);
        File.WriteAllText(modified, "new launcher link");
        Assert.Equal(completes, await LegacyInstallationService.PreserveShortcutsAsync([removed, modified, absent], () =>
        {
            File.Delete(removed); // 模拟旧卸载日志；不运行任何卸载程序。
            File.WriteAllText(modified, "user changed this while waiting");
            return Task.FromResult(completes);
        }));
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, File.ReadAllBytes(removed));
        Assert.Equal("user changed this while waiting", File.ReadAllText(modified));
        Assert.False(File.Exists(absent));
        Assert.Equal(2, Directory.GetFiles(paths.RootDirectory).Length);
    }

    [Fact]
    public async Task RestoresRemovedShortcutEvenWhenUninstallThrows()
    {
        using var paths = new MockAppDataPaths();
        var shortcut = Path.Combine(paths.RootDirectory, "new-menu.lnk");
        File.WriteAllText(shortcut, "new launcher link");
        await Assert.ThrowsAsync<Win32Exception>(() =>
            LegacyInstallationService.PreserveShortcutsAsync([shortcut], () =>
            {
                File.Delete(shortcut);
                throw new Win32Exception(1223);
            }));
        Assert.Equal("new launcher link", File.ReadAllText(shortcut));
    }

    internal static InstalledAppContext CreateContext(MockAppDataPaths paths, bool installed)
    {
        var root = Path.Combine(paths.RootDirectory, "install");
        var directory = Path.Combine(root, installed ? "app-3.0.0" : "debug");
        Directory.CreateDirectory(directory);
        // Fake state and exe live exclusively in fixture-owned TEMP. Never execute the exe.
        File.WriteAllText(Path.Combine(root, "state.json"),
            """{"schema":1,"current":"3.0.0","currentHealthy":false,"failedStarts":0,"failedVersions":[]}""");
        var executable = Path.Combine(directory, "AniMeido.App.exe");
        File.WriteAllText(executable, "test-only, never executed");
        return new InstalledAppContext(executable);
    }

    private static readonly UninstallEntry Entry = new(@"D:\test-only", "\"D:\\test-only\\unins000.exe\"");
    private static Task<bool> NeverRun(ProcessStartInfo _) => throw new InvalidOperationException("Must not start processes.");
}

internal sealed class FakeInstallationRegistry : IInstallationRegistry
{
    internal Dictionary<RegistryHive, UninstallEntry> Entries { get; } = [];
    internal List<RegistryHive> Reads { get; } = [];
    internal RegistryHive? FailureHive { get; init; }
    internal string? WrittenVersion { get; private set; }
    internal bool KeyExists { get; init; } = true;
    internal bool FailWrite { get; init; }

    public UninstallEntry? ReadLegacyInstallation(RegistryHive hive)
    {
        Reads.Add(hive);
        if (hive == FailureHive)
            throw new UnauthorizedAccessException("test-only read failure");
        return Entries.GetValueOrDefault(hive);
    }

    public void UpdateDisplayVersionIfPresent(string version)
    {
        if (FailWrite)
            throw new UnauthorizedAccessException("test-only write failure");
        if (KeyExists)
            WrittenVersion = version;
    }
}
