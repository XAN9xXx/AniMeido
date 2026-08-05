using AniMeido.App.Models;
using AniMeido.App.Services;
using AniMeido.PluginProtocol;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class PluginPackageManagerTests : IDisposable
{
    private const string PluginId = "AniMeido.Plugin.Test";

    private readonly string _rootDirectory;
    private readonly PluginPackageManager _manager;

    public PluginPackageManagerTests()
    {
        _rootDirectory = Path.Combine(
            Path.GetTempPath(),
            "AniMeido.Tests",
            Guid.NewGuid().ToString("N"));
        var verifier = new PluginPackageVerifier(new Version(1, 1, 0));
        _manager = new PluginPackageManager(
            new PluginInstallationPaths(_rootDirectory),
            verifier);
    }

    [Fact]
    public async Task InstallPackage_ValidPackage_InstallsAndRequiresRestart()
    {
        var package = await CreatePackageAsync("1.0.0");

        var result = await _manager.InstallPackageAsync(package);
        var installed = await _manager.GetInstalledPluginsAsync();

        Assert.Equal(PluginId, result.PluginId);
        Assert.False(result.IsUpgrade);
        Assert.True(result.RestartRequired);
        var plugin = Assert.Single(installed);
        Assert.Equal("1.0.0", plugin.CurrentVersion);
        Assert.True(plugin.Enabled);
    }

    [Fact]
    public async Task InstallPackage_TamperedDependency_IsRejected()
    {
        var package = await CreatePackageAsync(
            "1.0.0",
            tamperFileAfterManifestCreation: "dependency.dll");

        var exception = await Assert.ThrowsAsync<PluginOperationException>(
            () => _manager.InstallPackageAsync(package));

        Assert.Contains("校验失败", exception.Message, StringComparison.Ordinal);
        Assert.Empty(await _manager.GetInstalledPluginsAsync());
    }

    [Fact]
    public async Task InstallPackage_PathTraversal_IsRejectedBeforeExtraction()
    {
        var package = await CreatePackageAsync(
            "1.0.0",
            additionalManifestFile: new PluginPackageFile
            {
                Path = "../outside.dll",
                Sha256 = new string('0', 64),
            });

        await Assert.ThrowsAsync<PluginOperationException>(
            () => _manager.InstallPackageAsync(package));

        Assert.False(File.Exists(Path.Combine(_rootDirectory, "outside.dll")));
    }

    [Fact]
    public async Task UpgradeRollbackDisableAndUninstall_AreAppliedAcrossRestartBoundary()
    {
        var version1 = await CreatePackageAsync("1.0.0");
        var version2 = await CreatePackageAsync("1.1.0");
        await _manager.InstallPackageAsync(version1);
        await _manager.InstallPackageAsync(version2);

        var upgraded = Assert.Single(await _manager.GetInstalledPluginsAsync());
        Assert.Equal("1.1.0", upgraded.CurrentVersion);
        Assert.Equal("1.0.0", upgraded.PreviousVersion);

        await _manager.RollbackAsync(PluginId);
        var rolledBack = Assert.Single(await _manager.GetInstalledPluginsAsync());
        Assert.Equal("1.0.0", rolledBack.CurrentVersion);

        await _manager.SetEnabledAsync(PluginId, false);
        Assert.Empty(await _manager.PrepareForStartupAsync());

        await _manager.RequestUninstallAsync(PluginId);
        Assert.Empty(await _manager.PrepareForStartupAsync());
        Assert.Empty(await _manager.GetInstalledPluginsAsync());
        Assert.False(Directory.Exists(
            Path.Combine(_rootDirectory, "installed", PluginId)));
    }

    [Fact]
    public async Task PrepareForStartup_ModifiedInstalledFile_DisablesPlugin()
    {
        var package = await CreatePackageAsync("1.0.0");
        await _manager.InstallPackageAsync(package);
        var entryAssembly = Path.Combine(
            _rootDirectory,
            "installed",
            PluginId,
            "versions",
            "1.0.0",
            $"{PluginId}.dll");
        await File.WriteAllTextAsync(entryAssembly, "tampered");

        var loadDirectories = await _manager.PrepareForStartupAsync();
        var state = Assert.Single(await _manager.GetInstalledPluginsAsync());

        Assert.Empty(loadDirectories);
        Assert.False(state.Enabled);
        Assert.NotNull(state.LastError);
    }

    [Fact]
    public void ValidateManifest_InvalidMinimumVersion_IsRejected()
    {
        var verifier = new PluginPackageVerifier(new Version(1, 1, 0));
        var manifest = CreateManifest("1.0.0");
        manifest.MinAppVersion = "not-a-version";

        Assert.Throws<PluginOperationException>(
            () => verifier.ValidateManifest(manifest));
    }

    [Fact]
    public void ValidateManifest_V1Package_IsRejectedWithMigrationMessage()
    {
        var verifier = new PluginPackageVerifier(new Version(1, 3, 0));
        var manifest = CreateManifest("1.0.0");
        manifest.FormatVersion = 1;

        var exception = Assert.Throws<PluginOperationException>(
            () => verifier.ValidateManifest(manifest));

        Assert.Contains("v1 已停用", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateManifest_CommandWithoutActivationEvent_IsRejected()
    {
        var verifier = new PluginPackageVerifier(new Version(1, 3, 0));
        var manifest = CreateManifest("1.0.0");
        manifest.Contributions.Commands.Add(new PluginCommandContribution
        {
            Id = $"{PluginId}.open",
            Title = "Open",
            Icon = "\uE8A7",
        });

        var exception = Assert.Throws<PluginOperationException>(
            () => verifier.ValidateManifest(manifest));

        Assert.Contains("缺少激活事件", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateManifest_PlaybackContribution_IsAccepted()
    {
        var verifier = new PluginPackageVerifier(new Version(1, 3, 0));
        var manifest = CreateManifest("1.0.0");
        manifest.ActivationEvents.Add(
            PluginHostProtocol.AnimePlaybackActivationEvent);
        manifest.Contributions.Capabilities.Add(
            PluginHostProtocol.AnimePlaybackCapability);

        verifier.ValidateManifest(manifest);
    }

    [Fact]
    public void ValidateManifest_PersonalAnimeDataCapability_IsAccepted()
    {
        var verifier = new PluginPackageVerifier(new Version(1, 7, 0));
        var manifest = CreateManifest("1.0.0");
        manifest.Contributions.Capabilities.Add(
            PluginHostProtocol.PersonalAnimeDataCapability);

        verifier.ValidateManifest(manifest);
    }

    [Fact]
    public void ValidateManifest_UnknownCapability_IsRejected()
    {
        var verifier = new PluginPackageVerifier(new Version(1, 7, 0));
        var manifest = CreateManifest("1.0.0");
        manifest.Contributions.Capabilities.Add("arbitraryCapability");

        var exception = Assert.Throws<PluginOperationException>(
            () => verifier.ValidateManifest(manifest));

        Assert.Contains(
            "不支持的插件能力",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateManifest_SettingsContribution_IsAccepted()
    {
        var verifier = new PluginPackageVerifier(new Version(1, 6, 0));
        var manifest = CreateManifest("1.0.0");
        var settingsId = $"{PluginId}.settings";
        manifest.Contributions.Settings.Add(new PluginSettingsContribution
        {
            Id = settingsId,
            Title = "Plugin settings",
            Icon = "\uE713",
        });
        manifest.ActivationEvents.Add(
            PluginHostProtocol.SettingsActivationPrefix + settingsId);

        verifier.ValidateManifest(manifest);
    }

    [Fact]
    public void ValidateManifest_SettingsWithoutActivationEvent_IsRejected()
    {
        var verifier = new PluginPackageVerifier(new Version(1, 6, 0));
        var manifest = CreateManifest("1.0.0");
        manifest.Contributions.Settings.Add(new PluginSettingsContribution
        {
            Id = $"{PluginId}.settings",
            Title = "Plugin settings",
            Icon = "\uE713",
        });

        var exception = Assert.Throws<PluginOperationException>(
            () => verifier.ValidateManifest(manifest));

        Assert.Contains(
            "设置缺少激活事件",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateManifest_DuplicateSettingsContribution_IsRejected()
    {
        var verifier = new PluginPackageVerifier(new Version(1, 6, 0));
        var manifest = CreateManifest("1.0.0");
        var settingsId = $"{PluginId}.settings";
        manifest.Contributions.Settings.Add(new PluginSettingsContribution
        {
            Id = settingsId,
            Title = "Plugin settings",
            Icon = "\uE713",
        });
        manifest.Contributions.Settings.Add(new PluginSettingsContribution
        {
            Id = settingsId,
            Title = "Duplicate settings",
            Icon = "\uE713",
        });
        manifest.ActivationEvents.Add(
            PluginHostProtocol.SettingsActivationPrefix + settingsId);

        var exception = Assert.Throws<PluginOperationException>(
            () => verifier.ValidateManifest(manifest));

        Assert.Contains(
            "设置贡献无效或重复",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"formatVersion":2,"files":null}""")]
    [InlineData("""{"formatVersion":2,"activationEvents":null}""")]
    [InlineData("""{"formatVersion":2,"contributes":null}""")]
    [InlineData(
        """{"formatVersion":2,"contributes":{"commands":[],"navigation":[],"settings":null,"capabilities":[]}}""")]
    public void ValidateManifest_NullCollections_AreRejectedWithDomainError(
        string json)
    {
        var verifier = new PluginPackageVerifier(new Version(1, 3, 0));
        var manifest = PluginManifest.Load(json);

        Assert.NotNull(manifest);
        Assert.Throws<PluginOperationException>(
            () => verifier.ValidateManifest(manifest!));
    }

    [Fact]
    public async Task ReadRegistry_NullPluginCollection_IsRejectedWithDomainError()
    {
        Directory.CreateDirectory(_rootDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(_rootDirectory, "state.json"),
            """{"formatVersion":1,"plugins":null}""");

        await Assert.ThrowsAsync<PluginOperationException>(
            () => _manager.GetInstalledPluginsAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }

    private async Task<string> CreatePackageAsync(
        string version,
        string? tamperFileAfterManifestCreation = null,
        PluginPackageFile? additionalManifestFile = null)
    {
        Directory.CreateDirectory(_rootDirectory);
        var packagePath = Path.Combine(
            _rootDirectory,
            $"{Guid.NewGuid():N}.animeido-plugin");
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [$"{PluginId}.dll"] = Encoding.UTF8.GetBytes($"plugin-{version}"),
            ["dependency.dll"] = Encoding.UTF8.GetBytes($"dependency-{version}"),
            ["Assets/icon.txt"] = Encoding.UTF8.GetBytes("asset"),
        };
        var manifest = CreateManifest(version, contents);
        if (additionalManifestFile is not null)
        {
            manifest.Files.Add(additionalManifestFile);
        }

        await using var stream = File.Create(packagePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        var manifestEntry = archive.CreateEntry("plugin.json");
        await using (var manifestStream = manifestEntry.Open())
        {
            await JsonSerializer.SerializeAsync(manifestStream, manifest);
        }

        foreach (var (path, originalContent) in contents)
        {
            var content = string.Equals(
                path,
                tamperFileAfterManifestCreation,
                StringComparison.Ordinal)
                ? Encoding.UTF8.GetBytes("tampered")
                : originalContent;
            var entry = archive.CreateEntry(path);
            await using var entryStream = entry.Open();
            await entryStream.WriteAsync(content);
        }

        return packagePath;
    }

    private static PluginManifest CreateManifest(
        string version,
        IReadOnlyDictionary<string, byte[]>? contents = null)
    {
        contents ??= new Dictionary<string, byte[]>
        {
            [$"{PluginId}.dll"] = Encoding.UTF8.GetBytes("plugin"),
        };

        return new PluginManifest
        {
            PluginId = PluginId,
            DisplayName = "Test Plugin",
            Version = version,
            MinAppVersion = "1.1.0",
            EntryAssembly = $"{PluginId}.dll",
            Files = contents
                .Select(item => new PluginPackageFile
                {
                    Path = item.Key,
                    Sha256 = Convert.ToHexString(
                        SHA256.HashData(item.Value)).ToLowerInvariant(),
                })
                .ToList(),
        };
    }
}
