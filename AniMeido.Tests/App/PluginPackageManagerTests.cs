using AniMeido.App.Models;
using AniMeido.App.Services;
using AniMeido.PluginProtocol;
using System.IO.Compression;
using System.Reflection;
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
            $"AniMeido.Tests-{Guid.NewGuid():N}");
        var verifier = new PluginPackageVerifier(new Version(1, 1, 0));
        _manager = new PluginPackageManager(
            new PluginInstallationPaths(_rootDirectory),
            verifier);
    }

    [Theory]
    [InlineData("startup")]
    [InlineData("list")]
    [InlineData("install")]
    [InlineData("enable")]
    [InlineData("rollback")]
    [InlineData("uninstall")]
    [InlineData("load-failure")]
    [Trait("Category", "Mechanism")]
    public async Task CompleteOperationDoesNotRequireTheCallingUiContext(string action)
    {
        await _manager.InstallPackageAsync(await CreatePackageAsync("1.0.0"));
        await _manager.InstallPackageAsync(await CreatePackageAsync("1.1.0"));
        var package = await CreatePackageAsync("1.2.0");
        var gate = (SemaphoreSlim)typeof(PluginPackageManager)
            .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_manager)!;
        var ui = new PausedUiContext();
        var previousContext = SynchronizationContext.Current;
        Task operation;
        await gate.WaitAsync();
        try
        {
            // 确定操作会等待锁，随后 UI 不处理消息，避免依赖文件 I/O 的完成时机。
            SynchronizationContext.SetSynchronizationContext(ui);
            operation = action switch
            {
                "startup" => _manager.PrepareForStartupAsync(),
                "list" => _manager.GetInstalledPluginsAsync(),
                "install" => _manager.InstallPackageAsync(package),
                "enable" => _manager.SetEnabledAsync(PluginId, true),
                "rollback" => _manager.RollbackAsync(PluginId),
                "uninstall" => _manager.RequestUninstallAsync(PluginId),
                "load-failure" => _manager.RecordLoadFailuresAsync(
                    new Dictionary<string, string> { [PluginId] = "synthetic failure" }),
                _ => throw new ArgumentOutOfRangeException(nameof(action)),
            };
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            gate.Release();
        }

        try
        {
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, ui.PostCount);
        }
        finally
        {
            ui.Resume();
            await Task.WhenAny(operation).WaitAsync(TimeSpan.FromSeconds(5));
            await operation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }

        var installed = Assert.Single(await _manager.GetInstalledPluginsAsync());
        Assert.Equal(PluginId, installed.PluginId);
        var expectedVersion = action switch
        {
            "install" => "1.2.0",
            "rollback" => "1.0.0",
            _ => "1.1.0",
        };
        Assert.Equal(expectedVersion, installed.CurrentVersion);
        Assert.Equal(action == "uninstall", installed.PendingUninstall);
    }

    [Fact]
    public async Task InstallPackage_BackupFailureAfterRegistryCommitKeepsInstalledVersion()
    {
        var package = await CreatePackageAsync("1.0.0");
        Directory.CreateDirectory(Path.Combine(_rootDirectory, "state.backup.json"));

        var result = await _manager.InstallPackageAsync(package);

        Assert.Equal(PluginId, result.PluginId);
        Assert.Equal("1.0.0", Assert.Single(await _manager.GetInstalledPluginsAsync()).CurrentVersion);
        Assert.True(Directory.Exists(new PluginInstallationPaths(_rootDirectory).GetVersionDirectory(PluginId, "1.0.0")));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_rootDirectory, "quarantine")));
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
    public async Task StartupReturnsTheVerifiedManifestWithoutCachingItAcrossRuns()
    {
        await _manager.InstallPackageAsync(await CreatePackageAsync("1.0.0"));

        var prepared = Assert.Single(await _manager.PrepareForStartupAsync());

        Assert.Equal(Path.Combine(_rootDirectory, "installed", PluginId, "versions", "1.0.0"), prepared.Directory);
        Assert.Equal(PluginId, prepared.Manifest.PluginId);
        Assert.Equal("1.0.0", prepared.Manifest.Version);
        Assert.Equal($"{PluginId}.dll", prepared.Manifest.EntryAssembly);
        Assert.Equal(3, prepared.Manifest.Files.Count);
        prepared.Manifest.DisplayName = "not persisted";

        var nextRun = Assert.Single(await _manager.PrepareForStartupAsync());
        Assert.NotSame(prepared.Manifest, nextRun.Manifest);
        Assert.Equal("Test Plugin", nextRun.Manifest.DisplayName);
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

    [Theory]
    [InlineData("Entry.dll", "Entry.dll", true)]
    [InlineData("sub/Entry.dll", "sub/Entry.dll", false)]
    [InlineData(@"sub\Entry.dll", "sub/Entry.dll", false)]
    public void ValidateManifest_EntryAssemblyMustBeARootFile(
        string entryAssembly,
        string manifestFilePath,
        bool isRootFile)
    {
        var verifier = new PluginPackageVerifier(new Version(1, 1, 0));
        var manifest = new PluginManifest
        {
            PluginId = PluginId,
            DisplayName = "Test Plugin",
            Version = "1.0.0",
            MinAppVersion = "1.1.0",
            EntryAssembly = entryAssembly,
            Files =
            [
                new PluginPackageFile
                {
                    Path = manifestFilePath,
                    Sha256 = new string('0', 64),
                },
            ],
        };

        if (isRootFile)
        {
            verifier.ValidateManifest(manifest);
            return;
        }

        var exception = Assert.Throws<PluginOperationException>(
            () => verifier.ValidateManifest(manifest));

        Assert.Contains("包根目录", exception.Message, StringComparison.Ordinal);
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
