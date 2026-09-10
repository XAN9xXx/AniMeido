using AniMeido.Plugin.Player.Sources.Packages;
using System.IO.Compression;
using System.Text;

namespace AniMeido.Tests;

public sealed class SourcePackageInstallerTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        $"animeido-source-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task InstallAsync_InstallsApiRuleAndSupportsUpdate()
    {
        var sourcesDirectory = Path.Combine(_testDirectory, "Sources");
        var installer = new SourcePackageInstaller(sourcesDirectory);
        var firstPackage = CreatePackage(
            "example.source",
            "1.0.0",
            """{"formatVersion":1,"id":"example.source"}""");
        var secondPackage = CreatePackage(
            "example.source",
            "1.1.0",
            """{"formatVersion":1,"id":"example.source","updated":true}""");

        var firstResult = await installer.InstallAsync(
            firstPackage,
            CancellationToken.None);
        var firstInstalled = Assert.Single(await installer.ListAsync(
            CancellationToken.None));
        await installer.SetEnabledAsync(
            firstInstalled.Id,
            enabled: false,
            cancellationToken: CancellationToken.None);
        var secondResult = await installer.InstallAsync(
            secondPackage,
            CancellationToken.None);

        Assert.Equal("测试源 1.0.0", firstResult);
        Assert.Equal("测试源 1.1.0", secondResult);
        var updated = Assert.Single(await installer.ListAsync(
            CancellationToken.None));
        Assert.Equal(new Version(1, 1, 0), updated.Version);
        Assert.False(updated.IsEnabled);
        Assert.Empty(SourcePackageDiscovery
            .EnumerateEnabledPackageDirectories(sourcesDirectory));
        var installedRule = Path.Combine(
            sourcesDirectory,
            "Packages",
            "example.source",
            "example.animeido-source.json");
        Assert.Contains(
            "\"updated\":true",
            await File.ReadAllTextAsync(installedRule));
        Assert.Empty(Directory.EnumerateDirectories(
            Path.Combine(sourcesDirectory, "Packages"),
            "*.backup-*",
            SearchOption.TopDirectoryOnly));

        await installer.SetEnabledAsync(
            updated.Id,
            enabled: true,
            cancellationToken: CancellationToken.None);
        Assert.Single(SourcePackageDiscovery
            .EnumerateEnabledPackageDirectories(sourcesDirectory));

        await installer.UninstallAsync(updated.Id, CancellationToken.None);
        Assert.Empty(await installer.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task InstallAsync_RejectsEntryOutsidePackage()
    {
        var package = CreatePackage(
            "example.source",
            "1.0.0",
            "{}",
            "../example.animeido-source.json");
        var installer = new SourcePackageInstaller(
            Path.Combine(_testDirectory, "Sources"));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => installer.InstallAsync(package, CancellationToken.None));

        Assert.Contains("路径", exception.Message);
    }

    [Fact]
    public async Task ListAsync_ReportsBrokenPackageAndAllowsUninstall()
    {
        var sourcesDirectory = Path.Combine(_testDirectory, "Sources");
        var packageDirectory = Path.Combine(
            sourcesDirectory,
            "Packages",
            "broken.source");
        Directory.CreateDirectory(packageDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(packageDirectory, "source-package.json"),
            "not-json");
        var installer = new SourcePackageInstaller(sourcesDirectory);

        var brokenPackage = Assert.Single(await installer.ListAsync(
            CancellationToken.None));

        Assert.False(brokenPackage.IsValid);
        Assert.NotNull(brokenPackage.Error);
        await installer.UninstallAsync(
            brokenPackage.Id,
            CancellationToken.None);
        Assert.Empty(await installer.ListAsync(CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    private string CreatePackage(
        string id,
        string version,
        string rule,
        string entryFile = "example.animeido-source.json")
    {
        Directory.CreateDirectory(_testDirectory);
        var packagePath = Path.Combine(
            _testDirectory,
            $"{Guid.NewGuid():N}.animeido-source");
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        WriteEntry(
            archive,
            "source-package.json",
            $$"""
              {
                "formatVersion": 1,
                "id": "{{id}}",
                "displayName": "测试源",
                "version": "{{version}}",
                "entryFile": "{{entryFile}}"
              }
              """);
        WriteEntry(
            archive,
            "example.animeido-source.json",
            rule);
        return packagePath;
    }

    private static void WriteEntry(
        ZipArchive archive,
        string path,
        string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(
            entry.Open(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }
}
