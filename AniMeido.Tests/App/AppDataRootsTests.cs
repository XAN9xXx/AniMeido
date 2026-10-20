using AniMeido.App.Services;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Player.Playback;
using AniMeido.Plugin.Player.Sources.Managed;
using AniMeido.Plugin.Player.Sources.Packages;
using AniMeido.Plugin.Player.Sources.Rules;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace AniMeido.Tests;

[Collection(DevDataRootCollection.Name)]
public sealed class AppDataRootsTests : IDisposable
{
    private readonly MockAppDataPaths _temporary = new();
    private const BindingFlags StaticMembers = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AllTwelveLocations_KeepTheirOriginalSuffixes(bool isolated)
    {
        var root = Path.Combine(_temporary.RootDirectory, "验收");
        using var environment = new DevDataRootEnvironment(isolated ? root : null);
        var roaming = isolated ? Path.Combine(root, "Roaming") : Original(Environment.SpecialFolder.ApplicationData);
        var local = isolated ? Path.Combine(root, "Local") : Original(Environment.SpecialFolder.LocalApplicationData);
        var pictures = isolated ? Path.Combine(root, "Pictures") : Original(Environment.SpecialFolder.MyPictures);

        var paths = new AppDataPaths();
        Assert.Equal(Path.Combine(roaming, "AniMeido.db"), paths.DatabasePath);
        Assert.Equal(Path.Combine(roaming, "Backups"), paths.BackupDirectory);
        Assert.Equal(Path.Combine(roaming, "logs"), paths.LogDirectory);
        Assert.Equal(Path.Combine(roaming, "logs"), StartupLogger.LogDirectory);
        Assert.Equal(Path.Combine(local, "Plugins"), PluginInstallationPaths.CreateDefault().RootDirectory);
        Assert.Equal(roaming, Read<string>(BuildAssembly("AniMeido.Launcher", "AniMeido"), "AniMeido.Launcher.Program", "DataDirectory"));
        Assert.Equal(Path.Combine(roaming, "logs"), Read<string>(BuildAssembly("AniMeido.PluginHost"), "AniMeido.PluginHost.HostLog", "Root"));
        Assert.Equal(Path.Combine(roaming, "cache", "images"), ImageCacheHelper.CacheDirectory);
        Assert.Equal(Path.Combine(pictures, "Screenshots"), ScreenshotSettings.CreateDefault().RootDirectory);
        Assert.Equal(Path.Combine(roaming, "id-migration-v9.json"), PlayerPublicIds.SharedMigrationPath);
        Assert.Equal(Path.Combine(roaming, "id-remap-log.json"), PlayerPublicIds.SharedRemapPath);
        Assert.Equal(Path.Combine(local, "Player", "Sources"), ManagedSourceLoader.SourcesDirectory);
        Assert.Equal(Path.Combine(local, "Player", "Sources"), SourcePackageInstaller.GetSourcesDirectory());
        Assert.Equal(Path.Combine(local, "Player", "Sources"), SourceRuleLoader.SourcesDirectory);
        Assert.Equal(Path.Combine(local, "Plugins", "AniMeido.Plugin.AI"),
            Read<string>(BuildAssembly("Plugins/AniMeido.Plugin.AI"), "AniMeido.Plugin.AI.Services.AiPluginPaths", "DefaultRootDirectory"));

        foreach (var assembly in RootAssemblies())
        {
            Assert.Equal(roaming, Read<string>(assembly, "AniMeido.AppDataRoots", "Roaming"));
            Assert.Equal(local, Read<string>(assembly, "AniMeido.AppDataRoots", "Local"));
            Assert.Equal(pictures, Read<string>(assembly, "AniMeido.AppDataRoots", "Pictures"));
            Assert.Equal(isolated, Read<bool>(assembly, "AniMeido.AppDataRoots", "IsIsolated"));
            Assert.Equal(isolated ? "（独立数据：验收）" : "", Read<string>(assembly, "AniMeido.AppDataRoots", "DisplaySuffix"));
        }
        // 默认路径测试仅比较字符串，不初始化日志、缓存下载器或 AI 存储。
        Assert.Equal(isolated, Directory.Exists(root));
    }

    [Fact]
    public async Task BasePublishesRemaps_AndPlayerReadsTheSameIsolatedFile()
    {
        var root = Path.Combine(_temporary.RootDirectory, "remap");
        using var environment = new DevDataRootEnvironment(root);
        var paths = new AppDataPaths();
        using var ownedDatabase = new MockAppDataPaths(paths.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.DatabasePath)!);
        var factory = new SqliteConnectionFactory(paths);
        await new DatabaseService(factory, paths).InitializeAsync();
        using (var connection = await factory.OpenAsync())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO config(Key,Value) VALUES(@key,@value)";
            command.Parameters.AddWithValue("@key", PersonalIdRemapService.LogConfigKey);
            command.Parameters.AddWithValue("@value", """[{"seq":1,"from":-7,"to":8,"at":"2026-10-20"}]""");
            await command.ExecuteNonQueryAsync();
        }
        await new PersonalIdRemapService(factory, NullLogger<PersonalIdRemapService>.Instance).FlushLogAsync();
        Assert.Equal(Path.Combine(Path.GetDirectoryName(paths.DatabasePath)!, "id-remap-log.json"), PlayerPublicIds.SharedRemapPath);
        var entry = Assert.Single(await PlayerPublicIds.ReadRemapsAsync(PlayerPublicIds.SharedRemapPath, default));
        Assert.Equal(-7, entry.From);
        Assert.Equal(8, entry.To);
    }

    [Fact]
    public async Task ScreenshotIsolation_ChangesOnlyTheDefault_NotPersistedUserSettings()
    {
        using var environment = new DevDataRootEnvironment(Path.Combine(_temporary.RootDirectory, "screenshots"));
        var factory = new SqliteConnectionFactory(_temporary);
        await new DatabaseService(factory, _temporary).InitializeAsync();
        var archive = new ArchiveService(factory);
        Assert.Equal(ScreenshotSettings.CreateDefault(), await archive.GetScreenshotSettingsAsync());
        var custom = new ScreenshotSettings(false, Path.Combine(_temporary.RootDirectory, "custom"), false, false);
        await archive.SaveScreenshotSettingsAsync(custom);
        Assert.Equal(custom, await archive.GetScreenshotSettingsAsync());
        Assert.False(Directory.Exists(custom.RootDirectory));
    }

    [Theory]
    [InlineData("relative")]
    [InlineData(@"C:relative")]
    [InlineData(@"\rooted-but-not-absolute")]
    [InlineData(" ")]
    public void RelativeValues_AreRejectedBeforeOpeningData(string value) => AssertRejected(value);

    [Fact]
    public void RealDataAndInstallationPaths_AreRejectedByStringBeforeAnyFilesystemAccess()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var forbidden in new[]
        {
            Original(Environment.SpecialFolder.ApplicationData),
            Original(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(local, "Programs", "AniMeido"),
        })
        {
            AssertRejected(forbidden);
            AssertRejected(Path.Combine(forbidden, "child"));
            AssertRejected(forbidden.ToUpperInvariant());
            AssertRejected(Path.Combine(forbidden, "..", "AniMeido", "child"));
        }
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temporary.RootDirectory));
    }

    [Fact]
    public void ExistingFileOrFileAncestor_IsRejectedWithoutChangingTheFile()
    {
        var file = Path.Combine(_temporary.RootDirectory, "file");
        File.WriteAllText(file, "untouched");
        AssertRejected(file);
        AssertRejected(Path.Combine(file, "child"));
        Assert.Equal("untouched", File.ReadAllText(file));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JunctionOrSymbolicLinkAtRootAncestorOrMappedDirectory_IsRejectedWithoutFollowingIt(bool symbolic)
    {
        var target = Path.Combine(_temporary.RootDirectory, "target");
        var link = Path.Combine(_temporary.RootDirectory, "junction");
        Directory.CreateDirectory(target);
        CreateLink(link, target, symbolic);
        try
        {
            AssertRejected(link);
            AssertRejected(Path.Combine(link, "child"));
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        }
        finally
        {
            Directory.Delete(link);
        }
        var root = Path.Combine(_temporary.RootDirectory, "root");
        Directory.CreateDirectory(root);
        link = Path.Combine(root, "Roaming");
        CreateLink(link, target, symbolic);
        try
        {
            AssertRejected(root);
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void DevicePathsAndAmbiguousDirectoryNames_AreRejected()
    {
        AssertRejected(@"\\?\" + Original(Environment.SpecialFolder.ApplicationData));
        AssertRejected(Path.Combine(_temporary.RootDirectory, "trailing-space "));
        AssertRejected(Path.Combine(_temporary.RootDirectory, "trailing-dot."));
        AssertRejected(Path.Combine(_temporary.RootDirectory, "stream:name"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temporary.RootDirectory));
    }

    [Fact]
    public void StartupWarning_ContainsTheIsolatedRoot_AndTrayUsesTheSameMarker()
    {
        var root = Path.Combine(_temporary.RootDirectory, "warning");
        using var environment = new DevDataRootEnvironment(root);
        var previous = Log.Logger;
        try
        {
            StartupLogger.Initialize();
            Log.CloseAndFlush();
            var logs = Directory.GetFiles(Path.Combine(root, "Roaming", "logs"), "*.log", SearchOption.AllDirectories);
            var text = string.Join("\n", logs.Select(File.ReadAllText));
            Assert.Contains("Warning", text);
            Assert.Contains(root, text);
        }
        finally
        {
            Log.Logger = previous;
        }
        var data = typeof(TrayIconService).GetMethod("CreateIconData", StaticMembers)!.Invoke(null, [nint.Zero, nint.Zero])!;
        Assert.Equal("AniMeido（独立数据：warning）", data.GetType().GetField("Tip")!.GetValue(data));
    }

    private static void AssertRejected(string value)
    {
        using var environment = new DevDataRootEnvironment(value);
        foreach (var assembly in RootAssemblies())
            foreach (var name in new[] { "Roaming", "Local", "Pictures", "IsIsolated" })
                Assert.NotNull(Record.Exception(() => Read<object>(assembly, "AniMeido.AppDataRoots", name)));
        Assert.NotNull(Record.Exception(StartupLogger.Initialize));
        Assert.NotNull(Record.Exception(() => new AppDataPaths().DatabasePath));
    }

    private static IEnumerable<Assembly> RootAssemblies()
    {
        yield return typeof(AppDataPaths).Assembly;
        yield return typeof(ScreenshotSettings).Assembly;
        yield return typeof(PlayerPublicIds).Assembly;
        yield return BuildAssembly("AniMeido.Launcher", "AniMeido");
        yield return BuildAssembly("AniMeido.PluginHost");
        yield return BuildAssembly("Plugins/AniMeido.Plugin.AI");
    }

    private static T Read<T>(Assembly assembly, string type, string property)
    {
        try
        {
            return (T)assembly.GetType(type, throwOnError: true)!.GetProperty(property, StaticMembers)!.GetValue(null)!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    internal static string BuildOutput(string project)
    {
#if DEBUG
        const string Configuration = "Debug";
#else
        const string Configuration = "Release";
#endif
        return Path.Combine(RepositoryRoot, project, "bin", "x64", Configuration);
    }

    internal static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AniMeido.slnx")))
                directory = directory.Parent;
            Assert.NotNull(directory);
            return directory.FullName;
        }
    }

    private static Assembly BuildAssembly(string project, string? name = null)
    {
        var output = BuildOutput(project);
        // 本机发布过的 RID 子目录会留下同名旧文件，取最近一次构建写出的那个。
        var file = Directory.GetFiles(output, (name ?? Path.GetFileName(project)) + ".dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        Assert.NotNull(file);
        return Assembly.LoadFrom(file);
    }

    private static string Original(Environment.SpecialFolder folder)
        => Path.Combine(Environment.GetFolderPath(folder), "AniMeido");

    private static void CreateLink(string path, string target, bool symbolic)
    {
        if (symbolic)
        {
            Directory.CreateSymbolicLink(path, target);
            return;
        }
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{path}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        Assert.True(process.WaitForExit(5000));
        Assert.Equal(0, process.ExitCode);
    }

    public void Dispose() => _temporary.Dispose();
}
