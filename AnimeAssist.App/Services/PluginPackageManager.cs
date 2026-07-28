using AniMeido.App.Models;
using AniMeido.PluginProtocol;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace AniMeido.App.Services;

public sealed class PluginPackageManager
{
    private const int MaximumManifestSize = 256 * 1024;

    private readonly PluginInstallationPaths _paths;
    private readonly PluginPackageVerifier _verifier;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal PluginPackageManager(
        PluginInstallationPaths paths,
        PluginPackageVerifier verifier)
    {
        _paths = paths;
        _verifier = verifier;
    }

    public bool RestartRequired { get; private set; }

    public void MarkReloadApplied() => RestartRequired = false;

    public static PluginPackageManager CreateDefault()
    {
        var appVersion = System.Reflection.Assembly
            .GetEntryAssembly()?
            .GetName()
            .Version ?? new Version(0, 0, 0);
        return new PluginPackageManager(
            PluginInstallationPaths.CreateDefault(),
            new PluginPackageVerifier(appVersion));
    }

    public async Task<IReadOnlyList<string>> PrepareForStartupAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _paths.EnsureDirectories();
            CleanStagingDirectory();
            var registry = await LoadRegistryAsync(cancellationToken);
            var changed = false;

            foreach (var plugin in registry.Plugins.ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (plugin.PendingUninstall)
                {
                    try
                    {
                        var pluginDirectory = _paths.GetPluginDirectory(plugin.PluginId);
                        if (Directory.Exists(pluginDirectory))
                        {
                            Directory.Delete(pluginDirectory, recursive: true);
                        }

                        registry.Plugins.Remove(plugin);
                        changed = true;
                    }
                    catch (Exception ex) when (
                        ex is IOException
                        or UnauthorizedAccessException)
                    {
                        plugin.LastError = $"卸载失败：{ex.Message}";
                        changed = true;
                    }

                    continue;
                }

                if (!plugin.Enabled)
                {
                    continue;
                }

                try
                {
                    var versionDirectory = _paths.GetVersionDirectory(
                        plugin.PluginId,
                        plugin.CurrentVersion);
                    await _verifier.VerifyInstalledDirectoryAsync(
                        versionDirectory,
                        cancellationToken);
                    if (plugin.LastError is not null)
                    {
                        plugin.LastError = null;
                        changed = true;
                    }
                }
                catch (PluginOperationException ex)
                {
                    plugin.Enabled = false;
                    plugin.LastError = ex.Message;
                    changed = true;
                }
            }

            if (changed)
            {
                await SaveRegistryAsync(registry, cancellationToken);
            }

            return registry.Plugins
                .Where(plugin => plugin.Enabled && !plugin.PendingUninstall)
                .Select(plugin => _paths.GetVersionDirectory(
                    plugin.PluginId,
                    plugin.CurrentVersion))
                .Where(Directory.Exists)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<InstalledPluginInfo>> GetInstalledPluginsAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _paths.EnsureDirectories();
            var registry = await LoadRegistryAsync(cancellationToken);
            return registry.Plugins
                .OrderBy(plugin => plugin.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(ToInfo)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PluginInstallResult> InstallPackageAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);

        await _gate.WaitAsync(cancellationToken);
        string? stagingDirectory = null;
        try
        {
            _paths.EnsureDirectories();
            stagingDirectory = Path.Combine(
                _paths.StagingDirectory,
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDirectory);

            var manifest = await ExtractAndValidatePackageAsync(
                packagePath,
                stagingDirectory,
                cancellationToken);
            var registry = await LoadRegistryAsync(cancellationToken);
            var existing = registry.Plugins.FirstOrDefault(plugin =>
                string.Equals(
                    plugin.PluginId,
                    manifest.PluginId,
                    StringComparison.OrdinalIgnoreCase));
            var versionDirectory = _paths.GetVersionDirectory(
                manifest.PluginId,
                manifest.Version);

            if (Directory.Exists(versionDirectory))
            {
                var isReferenced = existing is not null
                    && (string.Equals(
                            existing.CurrentVersion,
                            manifest.Version,
                            StringComparison.Ordinal)
                        || string.Equals(
                            existing.PreviousVersion,
                            manifest.Version,
                            StringComparison.Ordinal));
                if (isReferenced)
                {
                    throw new PluginOperationException(
                        $"插件 {manifest.DisplayName} {manifest.Version} 已安装。");
                }

                MoveToQuarantine(
                    versionDirectory,
                    $"{manifest.PluginId}-{manifest.Version}-orphan");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(versionDirectory)!);
            Directory.Move(stagingDirectory, versionDirectory);
            stagingDirectory = null;

            var isUpgrade = existing is not null;
            if (existing is null)
            {
                registry.Plugins.Add(new InstalledPluginState
                {
                    PluginId = manifest.PluginId,
                    DisplayName = manifest.DisplayName,
                    CurrentVersion = manifest.Version,
                    Enabled = true,
                    InstalledAtUtc = DateTimeOffset.UtcNow,
                    SourceFileName = Path.GetFileName(packagePath),
                });
            }
            else
            {
                existing.DisplayName = manifest.DisplayName;
                existing.PreviousVersion = existing.CurrentVersion;
                existing.CurrentVersion = manifest.Version;
                existing.Enabled = true;
                existing.PendingUninstall = false;
                existing.InstalledAtUtc = DateTimeOffset.UtcNow;
                existing.SourceFileName = Path.GetFileName(packagePath);
                existing.LastError = null;
            }

            try
            {
                await SaveRegistryAsync(registry, cancellationToken);
            }
            catch
            {
                if (Directory.Exists(versionDirectory))
                {
                    try
                    {
                        MoveToQuarantine(
                            versionDirectory,
                            $"{manifest.PluginId}-{manifest.Version}-state-failed");
                    }
                    catch (IOException)
                    {
                        // Preserve the original state-write exception.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Preserve the original state-write exception.
                    }
                }

                throw;
            }

            RestartRequired = true;
            return new PluginInstallResult(
                manifest.PluginId,
                manifest.DisplayName,
                manifest.Version,
                isUpgrade,
                RestartRequired: true);
        }
        finally
        {
            _gate.Release();
            if (stagingDirectory is not null && Directory.Exists(stagingDirectory))
            {
                try
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
                catch (Exception ex) when (
                    ex is IOException
                    or UnauthorizedAccessException)
                {
                    // A later startup can clean an abandoned staging directory.
                }
            }
        }
    }

    public Task SetEnabledAsync(
        string pluginId,
        bool enabled,
        CancellationToken cancellationToken = default)
        => UpdatePluginAsync(
            pluginId,
            async (plugin, token) =>
            {
                if (enabled)
                {
                    var directory = _paths.GetVersionDirectory(
                        plugin.PluginId,
                        plugin.CurrentVersion);
                    await _verifier.VerifyInstalledDirectoryAsync(directory, token);
                    plugin.LastError = null;
                }

                plugin.Enabled = enabled;
                plugin.PendingUninstall = false;
            },
            cancellationToken);

    public Task RollbackAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
        => UpdatePluginAsync(
            pluginId,
            async (plugin, token) =>
            {
                if (string.IsNullOrWhiteSpace(plugin.PreviousVersion))
                {
                    throw new PluginOperationException("没有可回滚的插件版本。");
                }

                var previousDirectory = _paths.GetVersionDirectory(
                    plugin.PluginId,
                    plugin.PreviousVersion);
                await _verifier.VerifyInstalledDirectoryAsync(previousDirectory, token);

                (plugin.CurrentVersion, plugin.PreviousVersion) =
                    (plugin.PreviousVersion, plugin.CurrentVersion);
                plugin.Enabled = true;
                plugin.PendingUninstall = false;
                plugin.LastError = null;
            },
            cancellationToken);

    public Task RequestUninstallAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
        => UpdatePluginAsync(
            pluginId,
            (plugin, _) =>
            {
                plugin.Enabled = false;
                plugin.PendingUninstall = true;
                return Task.CompletedTask;
            },
            cancellationToken);

    public async Task RecordLoadFailuresAsync(
        IReadOnlyDictionary<string, string> failures,
        CancellationToken cancellationToken = default)
    {
        if (failures.Count == 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var registry = await LoadRegistryAsync(cancellationToken);
            var changed = false;
            foreach (var (pluginId, error) in failures)
            {
                var plugin = registry.Plugins.FirstOrDefault(item =>
                    string.Equals(
                        item.PluginId,
                        pluginId,
                        StringComparison.OrdinalIgnoreCase));
                if (plugin is null)
                {
                    continue;
                }

                plugin.Enabled = false;
                plugin.LastError = $"加载失败：{error}";
                changed = true;
            }

            if (changed)
            {
                await SaveRegistryAsync(registry, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task UpdatePluginAsync(
        string pluginId,
        Func<InstalledPluginState, CancellationToken, Task> update,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var registry = await LoadRegistryAsync(cancellationToken);
            var plugin = registry.Plugins.FirstOrDefault(item =>
                string.Equals(item.PluginId, pluginId, StringComparison.OrdinalIgnoreCase))
                ?? throw new PluginOperationException("未找到指定插件。");
            await update(plugin, cancellationToken);
            await SaveRegistryAsync(registry, cancellationToken);
            RestartRequired = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PluginManifest> ExtractAndValidatePackageAsync(
        string packagePath,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var packageStream = new FileStream(
                packagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read);

            if (archive.Entries.Count is 0 or > PluginPackageVerifier.MaximumFileCount + 1)
            {
                throw new PluginOperationException("插件包文件数量无效。");
            }

            var manifestEntries = archive.Entries
                .Where(entry => string.Equals(
                    PluginManifest.NormalizePackagePath(entry.FullName),
                    "plugin.json",
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (manifestEntries.Count != 1)
            {
                throw new PluginOperationException("插件包必须包含一个根目录 plugin.json。");
            }

            var manifestEntry = manifestEntries[0];
            if (manifestEntry.Length > MaximumManifestSize)
            {
                throw new PluginOperationException("插件清单过大。");
            }

            PluginManifest manifest;
            await using (var manifestStream = manifestEntry.Open())
            using (var reader = new StreamReader(manifestStream))
            {
                var json = await reader.ReadToEndAsync(cancellationToken);
                manifest = PluginManifest.Load(json)
                    ?? throw new PluginOperationException("插件清单格式无效。");
            }

            _verifier.ValidateManifest(manifest);

            var manifestFiles = manifest.Files.ToDictionary(
                file => PluginManifest.NormalizePackagePath(file.Path),
                StringComparer.OrdinalIgnoreCase);
            var archiveFiles = archive.Entries
                .Where(entry => !string.IsNullOrEmpty(entry.Name))
                .Where(entry => !string.Equals(
                    PluginManifest.NormalizePackagePath(entry.FullName),
                    "plugin.json",
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (archiveFiles.Count != manifestFiles.Count)
            {
                throw new PluginOperationException("插件包文件与清单不一致。");
            }

            long totalSize = 0;
            foreach (var entry in archiveFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalizedPath = PluginManifest.NormalizePackagePath(entry.FullName);
                if (!manifestFiles.TryGetValue(normalizedPath, out var manifestFile))
                {
                    throw new PluginOperationException($"插件包包含清单外文件：{entry.FullName}");
                }

                if (entry.Length < 0 || entry.Length > PluginPackageVerifier.MaximumFileSize)
                {
                    throw new PluginOperationException($"插件文件过大：{entry.FullName}");
                }

                totalSize = checked(totalSize + entry.Length);
                if (totalSize > PluginPackageVerifier.MaximumPackageSize)
                {
                    throw new PluginOperationException("插件包解压后体积过大。");
                }

                var destination = PluginPackageVerifier.ResolveSafePath(
                    stagingDirectory,
                    normalizedPath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                await using (var input = entry.Open())
                await using (var output = new FileStream(
                    destination,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await input.CopyToAsync(output, cancellationToken);
                }

                var actualHash = await PluginPackageVerifier.ComputeFileHashAsync(
                    destination,
                    cancellationToken);
                if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(manifestFile.Sha256),
                    Convert.FromHexString(actualHash)))
                {
                    throw new PluginOperationException(
                        $"插件文件校验失败：{normalizedPath}");
                }
            }

            var installedManifestPath = Path.Combine(stagingDirectory, "plugin.json");
            await File.WriteAllTextAsync(
                installedManifestPath,
                JsonSerializer.Serialize(manifest, JsonOptions),
                cancellationToken);
            return manifest;
        }
        catch (PluginOperationException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or JsonException
            or OverflowException
            or FormatException)
        {
            throw new PluginOperationException("插件包读取或校验失败。", ex);
        }
    }

    private async Task<PluginRegistry> LoadRegistryAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.StateFile))
        {
            return new PluginRegistry();
        }

        try
        {
            return await ReadRegistryFileAsync(
                _paths.StateFile,
                cancellationToken);
        }
        catch (Exception primaryException) when (
            primaryException is PluginOperationException
            or IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            if (!File.Exists(_paths.StateBackupFile))
            {
                throw new PluginOperationException(
                    "无法读取插件状态文件。",
                    primaryException);
            }

            try
            {
                var backup = await ReadRegistryFileAsync(
                    _paths.StateBackupFile,
                    cancellationToken);
                File.Copy(
                    _paths.StateBackupFile,
                    _paths.StateFile,
                    overwrite: true);
                return backup;
            }
            catch (Exception backupException) when (
                backupException is PluginOperationException
                or IOException
                or UnauthorizedAccessException
                or JsonException)
            {
                throw new PluginOperationException(
                    "插件状态文件及其备份均无法读取。",
                    new AggregateException(primaryException, backupException));
            }
        }
    }

    private static async Task<PluginRegistry> ReadRegistryFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var registry = await JsonSerializer.DeserializeAsync<PluginRegistry>(
            stream,
            JsonOptions,
            cancellationToken);
        if (registry is null
            || registry.FormatVersion != PluginRegistry.CurrentFormatVersion)
        {
            throw new PluginOperationException("插件状态文件版本无效。");
        }

        if (registry.Plugins
            .Select(plugin => plugin.PluginId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() != registry.Plugins.Count)
        {
            throw new PluginOperationException("插件状态文件包含重复 ID。");
        }

        return registry;
    }

    private async Task SaveRegistryAsync(
        PluginRegistry registry,
        CancellationToken cancellationToken)
    {
        _paths.EnsureDirectories();
        var temporaryFile = Path.Combine(
            _paths.RootDirectory,
            $"state.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryFile,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    registry,
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (File.Exists(_paths.StateFile))
            {
                File.Copy(
                    _paths.StateFile,
                    _paths.StateBackupFile,
                    overwrite: true);
            }

            File.Move(temporaryFile, _paths.StateFile, overwrite: true);
            if (!File.Exists(_paths.StateBackupFile))
            {
                File.Copy(
                    _paths.StateFile,
                    _paths.StateBackupFile,
                    overwrite: false);
            }
        }
        finally
        {
            if (File.Exists(temporaryFile))
            {
                File.Delete(temporaryFile);
            }
        }
    }

    private static InstalledPluginInfo ToInfo(InstalledPluginState plugin)
        => new(
            plugin.PluginId,
            plugin.DisplayName,
            plugin.CurrentVersion,
            plugin.PreviousVersion,
            plugin.Enabled,
            plugin.PendingUninstall,
            plugin.InstalledAtUtc,
            plugin.SourceFileName,
            plugin.LastError);

    private void CleanStagingDirectory()
    {
        foreach (var directory in Directory.EnumerateDirectories(_paths.StagingDirectory))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Keep the abandoned staging directory for a later startup.
            }
            catch (UnauthorizedAccessException)
            {
                // Keep the abandoned staging directory for diagnostics.
            }
        }
    }

    private void MoveToQuarantine(string sourceDirectory, string label)
    {
        Directory.CreateDirectory(_paths.QuarantineDirectory);
        var safeLabel = string.Concat(label.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var destination = Path.Combine(
            _paths.QuarantineDirectory,
            $"{safeLabel}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
        Directory.Move(sourceDirectory, destination);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
}
