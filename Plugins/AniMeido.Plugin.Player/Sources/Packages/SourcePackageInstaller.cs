using System.IO.Compression;
using System.Text.Json;

namespace AniMeido.Plugin.Player.Sources.Packages;

/// <summary>
/// Installs a trusted local source package into PlayerPlugin-owned storage.
/// Changes take effect after AniMeido restarts.
/// </summary>
public sealed class SourcePackageInstaller
{
    internal const string OrphanedMarkerName = ".orphaned";
    internal const string UnmanagedMarkerName = ".unmanaged";
    private const int MaximumFileCount = 200;
    private const long MaximumFileSize = 64 * 1024 * 1024;
    private const long MaximumPackageSize = 128 * 1024 * 1024;
    private const long MaximumManifestSize = 64 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private readonly string _sourcesDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SourcePackageInstaller()
        : this(GetSourcesDirectory())
    {
    }

    internal SourcePackageInstaller(string sourcesDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcesDirectory);
        _sourcesDirectory = Path.GetFullPath(sourcesDirectory);
    }

    public async Task<string> InstallAsync(
        string packagePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        if (!string.Equals(
            Path.GetExtension(packagePath),
            ".animeido-source",
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "播放源包扩展名必须是 .animeido-source。");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await InstallCoreAsync(packagePath, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<InstalledSourcePackage>> ListAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var packagesDirectory = Path.Combine(_sourcesDirectory, "Packages");
            if (!Directory.Exists(packagesDirectory))
            {
                return [];
            }

            var packages = new List<InstalledSourcePackage>();
            foreach (var directory in Directory.EnumerateDirectories(
                packagesDirectory,
                "*",
                SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!SourcePackageDiscovery.IsPackageDirectory(directory))
                {
                    continue;
                }

                try
                {
                    var manifest = await ReadInstalledManifestAsync(
                        directory,
                        cancellationToken);
                    packages.Add(new InstalledSourcePackage(
                        manifest.Id,
                        manifest.DisplayName,
                        Version.Parse(manifest.Version),
                        !File.Exists(Path.Combine(
                            directory,
                            SourcePackageDiscovery.DisabledMarkerName)),
                        SourceKind: manifest.SourceKind,
                        SubscriptionId: manifest.SubscriptionId,
                        UpstreamPath: manifest.UpstreamPath,
                        UpstreamRevision: manifest.UpstreamRevision,
                        IsOrphaned: File.Exists(Path.Combine(
                            directory,
                            OrphanedMarkerName)),
                        IsUnmanaged: File.Exists(Path.Combine(
                            directory,
                            UnmanagedMarkerName)),
                        EntryFile: manifest.EntryFile));
                }
#pragma warning disable CA1031 // A malformed package must not hide valid packages.
                catch (Exception ex)
                {
                    var directoryName = Path.GetFileName(directory);
                    packages.Add(new InstalledSourcePackage(
                        directoryName,
                        $"无法读取的源包 ({directoryName})",
                        new Version(0, 0),
                        IsEnabled: false,
                        ex.Message));
                }
#pragma warning restore CA1031
            }

            return packages
                .OrderBy(package => package.DisplayName, StringComparer.CurrentCulture)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetEnabledAsync(
        string packageId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var packageDirectory = GetInstalledPackageDirectory(packageId);
            _ = await ReadInstalledManifestAsync(
                packageDirectory,
                cancellationToken);
            var markerPath = Path.Combine(
                packageDirectory,
                SourcePackageDiscovery.DisabledMarkerName);
            if (enabled)
            {
                if (File.Exists(markerPath))
                {
                    File.Delete(markerPath);
                }
            }
            else if (!File.Exists(markerPath))
            {
                await File.WriteAllTextAsync(
                    markerPath,
                    "Disabled by AniMeido PlayerPlugin.",
                    cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UninstallAsync(
        string packageId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var packageDirectory = GetInstalledPackageDirectory(packageId);
            var removedDirectory = $"{packageDirectory}.remove-{Guid.NewGuid():N}";
            Directory.Move(packageDirectory, removedDirectory);
            try
            {
                Directory.Delete(removedDirectory, recursive: true);
            }
            catch
            {
                if (!Directory.Exists(packageDirectory)
                    && Directory.Exists(removedDirectory))
                {
                    Directory.Move(removedDirectory, packageDirectory);
                }

                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task InstallSubscriptionSourceAsync(
        SourcePackageManifest manifest,
        string content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(content);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ValidateManifest(manifest);
            var packagesDirectory = Path.Combine(_sourcesDirectory, "Packages");
            Directory.CreateDirectory(packagesDirectory);
            var stagingDirectory = Path.Combine(
                packagesDirectory,
                $".install-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDirectory);
            try
            {
                await File.WriteAllTextAsync(
                    Path.Combine(stagingDirectory, "source-package.json"),
                    JsonSerializer.Serialize(manifest, SerializerOptions),
                    cancellationToken);
                var entryPath = ResolveSafePath(
                    stagingDirectory,
                    manifest.EntryFile);
                Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
                await File.WriteAllTextAsync(
                    entryPath,
                    content,
                    cancellationToken);
                await ReplaceInstalledDirectoryAsync(
                    stagingDirectory,
                    manifest,
                    disableWhenNew: true,
                    cancellationToken);
            }
            finally
            {
                if (Directory.Exists(stagingDirectory))
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task MarkOrphanedAsync(
        string packageId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var packageDirectory = GetInstalledPackageDirectory(packageId);
            await File.WriteAllTextAsync(
                Path.Combine(packageDirectory, OrphanedMarkerName),
                "The source is no longer present in its subscription.",
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(
                    packageDirectory,
                    SourcePackageDiscovery.DisabledMarkerName),
                "Disabled because the upstream source was removed.",
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task MarkUnmanagedAsync(
        string packageId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var packageDirectory = GetInstalledPackageDirectory(packageId);
            await File.WriteAllTextAsync(
                Path.Combine(packageDirectory, UnmanagedMarkerName),
                "The source subscription was removed.",
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(
                    packageDirectory,
                    SourcePackageDiscovery.DisabledMarkerName),
                "Disabled because the source subscription was removed.",
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static string GetSourcesDirectory()
        => Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "AniMeido",
            "Player",
            "Sources");

    private async Task<string> InstallCoreAsync(
        string packagePath,
        CancellationToken cancellationToken)
    {
        var packagesDirectory = Path.Combine(_sourcesDirectory, "Packages");
        Directory.CreateDirectory(packagesDirectory);
        var stagingDirectory = Path.Combine(
            packagesDirectory,
            $".install-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            var manifest = await ExtractAsync(
                packagePath,
                stagingDirectory,
                cancellationToken);
            var targetDirectory = ResolveSafePath(
                packagesDirectory,
                manifest.Id);
            var preserveDisabledState = File.Exists(Path.Combine(
                targetDirectory,
                SourcePackageDiscovery.DisabledMarkerName));
            var backupDirectory =
                $"{targetDirectory}.backup-{Guid.NewGuid():N}";
            if (Directory.Exists(targetDirectory))
            {
                Directory.Move(targetDirectory, backupDirectory);
            }

            try
            {
                Directory.Move(stagingDirectory, targetDirectory);
                if (preserveDisabledState)
                {
                    await File.WriteAllTextAsync(
                        Path.Combine(
                            targetDirectory,
                            SourcePackageDiscovery.DisabledMarkerName),
                        "Disabled by AniMeido PlayerPlugin.",
                        cancellationToken);
                }

                if (Directory.Exists(backupDirectory))
                {
                    TryDeleteDirectory(backupDirectory);
                }
            }
            catch
            {
                if (Directory.Exists(targetDirectory))
                {
                    TryDeleteDirectory(targetDirectory);
                }

                if (!Directory.Exists(targetDirectory)
                    && Directory.Exists(backupDirectory))
                {
                    Directory.Move(backupDirectory, targetDirectory);
                }

                throw;
            }

            return $"{manifest.DisplayName} {manifest.Version}";
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    private async Task ReplaceInstalledDirectoryAsync(
        string stagingDirectory,
        SourcePackageManifest manifest,
        bool disableWhenNew,
        CancellationToken cancellationToken)
    {
        var packagesDirectory = Path.Combine(_sourcesDirectory, "Packages");
        var targetDirectory = ResolveSafePath(packagesDirectory, manifest.Id);
        var targetExists = Directory.Exists(targetDirectory);
        var preserveDisabledState = targetExists
            && File.Exists(Path.Combine(
                targetDirectory,
                SourcePackageDiscovery.DisabledMarkerName));
        var backupDirectory = $"{targetDirectory}.backup-{Guid.NewGuid():N}";
        if (targetExists)
        {
            Directory.Move(targetDirectory, backupDirectory);
        }

        try
        {
            Directory.Move(stagingDirectory, targetDirectory);
            if (preserveDisabledState || (!targetExists && disableWhenNew))
            {
                await File.WriteAllTextAsync(
                    Path.Combine(
                        targetDirectory,
                        SourcePackageDiscovery.DisabledMarkerName),
                    "Disabled by AniMeido PlayerPlugin.",
                    cancellationToken);
            }

            if (Directory.Exists(backupDirectory))
            {
                TryDeleteDirectory(backupDirectory);
            }
        }
        catch
        {
            if (Directory.Exists(targetDirectory))
            {
                TryDeleteDirectory(targetDirectory);
            }

            if (!Directory.Exists(targetDirectory)
                && Directory.Exists(backupDirectory))
            {
                Directory.Move(backupDirectory, targetDirectory);
            }

            throw;
        }
    }

    private string GetInstalledPackageDirectory(string packageId)
    {
        var packagesDirectory = Path.Combine(_sourcesDirectory, "Packages");
        var packageDirectory = ResolveSafePath(packagesDirectory, packageId);
        if (!SourcePackageDiscovery.IsPackageDirectory(packageDirectory))
        {
            throw new DirectoryNotFoundException($"播放源包未安装：{packageId}");
        }

        return packageDirectory;
    }

    private static async Task<SourcePackageManifest> ReadInstalledManifestAsync(
        string packageDirectory,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(Path.Combine(
            packageDirectory,
            "source-package.json"));
        var manifest = await JsonSerializer.DeserializeAsync<SourcePackageManifest>(
            stream,
            SerializerOptions,
            cancellationToken)
            ?? throw new InvalidDataException("已安装播放源包清单格式无效。");
        ValidateManifest(manifest);
        if (!string.Equals(
            manifest.Id,
            Path.GetFileName(packageDirectory),
            StringComparison.Ordinal))
        {
            throw new InvalidDataException("播放源包目录与清单 ID 不一致。");
        }

        return manifest;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
#pragma warning disable CA1031 // Stale backup is ignored by discovery and may be cleaned later.
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    private static async Task<SourcePackageManifest> ExtractAsync(
        string packagePath,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        await using var packageStream = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read);
        if (archive.Entries.Count is 0 or > MaximumFileCount)
        {
            throw new InvalidDataException("播放源包文件数量无效。");
        }

        var manifestEntry = archive.Entries.SingleOrDefault(entry =>
            string.Equals(
                NormalizePath(entry.FullName),
                "source-package.json",
                StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                "播放源包根目录缺少 source-package.json。");
        if (manifestEntry.Length > MaximumManifestSize)
        {
            throw new InvalidDataException("播放源包清单过大。");
        }

        SourcePackageManifest manifest;
        await using (var manifestStream = manifestEntry.Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<SourcePackageManifest>(
                manifestStream,
                SerializerOptions,
                cancellationToken)
                ?? throw new InvalidDataException("播放源包清单格式无效。");
        }

        ValidateManifest(manifest);
        long totalSize = 0;
        foreach (var entry in archive.Entries.Where(entry =>
            !string.IsNullOrEmpty(entry.Name)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedPath = NormalizePath(entry.FullName);
            if (string.Equals(
                normalizedPath,
                "source-package.json",
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entry.Length is < 0 or > MaximumFileSize)
            {
                throw new InvalidDataException(
                    $"播放源包文件过大：{normalizedPath}");
            }

            totalSize = checked(totalSize + entry.Length);
            if (totalSize > MaximumPackageSize)
            {
                throw new InvalidDataException("播放源包解压后体积过大。");
            }

            var destination = ResolveSafePath(
                stagingDirectory,
                normalizedPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = entry.Open();
            await using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, cancellationToken);
        }

        var installedManifest = Path.Combine(
            stagingDirectory,
            "source-package.json");
        await File.WriteAllTextAsync(
            installedManifest,
            JsonSerializer.Serialize(manifest, SerializerOptions),
            cancellationToken);
        var entryPath = ResolveSafePath(
            stagingDirectory,
            manifest.EntryFile);
        if (!File.Exists(entryPath))
        {
            throw new InvalidDataException(
                $"播放源包入口不存在：{manifest.EntryFile}");
        }

        if (Path.GetExtension(entryPath).Equals(
            ".json",
            StringComparison.OrdinalIgnoreCase))
        {
            await using var entryStream = File.OpenRead(entryPath);
            using var document = await JsonDocument.ParseAsync(
                entryStream,
                cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "播放源包入口 JSON 必须是对象。");
            }
        }

        return manifest;
    }

    internal static void ValidateManifest(SourcePackageManifest manifest)
    {
        if (manifest.FormatVersion is not (1 or 2)
            || string.IsNullOrWhiteSpace(manifest.Id)
            || manifest.Id.Any(character =>
                !char.IsAsciiLetterOrDigit(character)
                && character is not '.' and not '-' and not '_')
            || string.IsNullOrWhiteSpace(manifest.DisplayName)
            || !Version.TryParse(manifest.Version, out _)
            || string.IsNullOrWhiteSpace(manifest.EntryFile)
            || Path.IsPathRooted(manifest.EntryFile))
        {
            throw new InvalidDataException("播放源包清单缺少或包含无效字段。");
        }

        var entryName = Path.GetFileName(manifest.EntryFile);
        if (manifest.FormatVersion == 1
            && !entryName.EndsWith(
                ".animeido-source.json",
                StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                entryName,
                "source.json",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "播放源包入口必须是 API 规则或代码源 source.json。");
        }

        if (manifest.FormatVersion == 2
            && (manifest.SourceKind is not
                    ("easybangumi-js" or "animeko-web-selector")
                || string.IsNullOrWhiteSpace(manifest.SubscriptionId)
                || string.IsNullOrWhiteSpace(manifest.UpstreamPath)
                || string.IsNullOrWhiteSpace(manifest.UpstreamRevision)
                || manifest.SourceKind == "easybangumi-js"
                    && !entryName.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                || manifest.SourceKind == "animeko-web-selector"
                    && !entryName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "订阅源包清单缺少来源信息或入口类型不匹配。");
        }
    }

    private static string NormalizePath(string path)
        => path.Replace('\\', '/').TrimStart('/');

    private static string ResolveSafePath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("播放源包只能使用相对路径。");
        }

        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!candidate.StartsWith(
            normalizedRoot,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("播放源包路径越过安装目录。");
        }

        return candidate;
    }
}
