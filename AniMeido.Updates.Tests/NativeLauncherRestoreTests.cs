using System.Diagnostics;
using System.Text.RegularExpressions;
using AniMeido.Updates.Install;
using Xunit;
using static AniMeido.Updates.Tests.RestoreDatabaseFixture;

namespace AniMeido.Updates.Tests;

public sealed class NativeLauncherRestoreTests
{
    [NativeLauncherTheory]
    [InlineData(7, true)]
    [InlineData(9, false)]
    public async Task PublishedLauncherUsesWalVersionAndItsOwnSqliteLibrary(int walVersion, bool expectedSuccess)
    {
        using var temp = new InstallTestRoot();
        var published = Environment.GetEnvironmentVariable(NativeLauncherTheoryAttribute.PathVariable)!;
        foreach (var name in new[] { "AniMeido.exe", "e_sqlite3.dll" })
            File.Copy(Path.Combine(published, name), temp.PathFor(name));

        var isolatedRoot = temp.PathFor("isolated");
        const string dataRelative = "isolated/Roaming";
        var database = MakeDatabase(temp, dataRelative + "/AniMeido.db", 6);
        var source = MakeWalSource(temp, 6, walVersion, includeSharedMemory: false, dataDirectory: dataRelative);
        var original = File.ReadAllBytes(database);
        var state = temp.Save();
        temp.Store.Write(state with
        {
            PendingRestore = new PendingRestoreRequest { Directory = source, RequestedAt = DateTimeOffset.UtcNow },
        });
        WriteSchemaAssembly(temp.PathFor("app-2.2.0/AniMeido.Plugin.Base.dll"), 8);
        // 不启动 WinUI；使用现有开发安装夹具相同的短命进程作为 App 替身。
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "whoami.exe"),
            temp.PathFor("app-2.2.0/AniMeido.App.exe"), overwrite: true);

        var start = new ProcessStartInfo(temp.PathFor("AniMeido.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = temp.Root,
        };
        start.Environment["ANIMEIDO_DATA_ROOT"] = isolatedRoot;
        using var launcher = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await launcher.WaitForExitAsync(timeout.Token);
            var log = File.ReadAllText(Path.Combine(isolatedRoot, "Roaming", "logs", "launcher.log"));
            Assert.True(launcher.ExitCode == 0, log);
            await WaitForAppStubAsync(log, timeout.Token);
            var restored = temp.Store.Read();
            Assert.Null(restored.PendingRestore);
            Assert.Equal(expectedSuccess, restored.LastRestore!.Success);
            if (expectedSuccess)
            {
                Assert.Equal(walVersion, DatabaseRollback.ReadUserVersion(database));
                Assert.NotNull(restored.LastRestore.Archive);
                using var connection = OpenDatabase(database);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Value FROM wal_evidence";
                Assert.Equal("committed in WAL", command.ExecuteScalar());
            }
            else
            {
                Assert.Contains("请先升级", restored.LastRestore.Error);
                Assert.Null(restored.LastRestore.Archive);
                Assert.Equal(original, File.ReadAllBytes(database));
            }
        }
        finally
        {
            if (!launcher.HasExited)
            {
                launcher.Kill(entireProcessTree: true);
                await launcher.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    private static async Task WaitForAppStubAsync(string log, CancellationToken cancellationToken)
    {
        var match = Regex.Match(log, @"（PID (\d+)）。");
        Assert.True(match.Success, log);
        Process child;
        try
        {
            child = Process.GetProcessById(int.Parse(match.Groups[1].Value));
        }
        catch (ArgumentException)
        {
            return;
        }

        using (child)
            await child.WaitForExitAsync(cancellationToken);
    }
}

internal sealed class NativeLauncherTheoryAttribute : TheoryAttribute
{
    internal const string PathVariable = "ANIMEIDO_TEST_NATIVE_LAUNCHER";

    public NativeLauncherTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PathVariable)))
            Skip = "需要先构建 Windows Native AOT 启动器，并通过 ANIMEIDO_TEST_NATIVE_LAUNCHER 指定本次产物目录。";
    }
}
