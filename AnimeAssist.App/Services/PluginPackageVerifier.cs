using AniMeido.PluginProtocol;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace AniMeido.App.Services;

internal sealed partial class PluginPackageVerifier
{
    public const int MaximumFileCount = 512;
    public const long MaximumFileSize = 256L * 1024 * 1024;
    public const long MaximumPackageSize = 512L * 1024 * 1024;

    private readonly Version _appVersion;

    public PluginPackageVerifier(Version appVersion)
    {
        _appVersion = appVersion;
    }

    public void ValidateManifest(PluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.FormatVersion != PluginManifest.CurrentFormatVersion)
        {
            if (manifest.FormatVersion == 1)
            {
                throw new PluginOperationException(
                    "插件包格式 v1 已停用。请安装面向 PluginHost 重新打包的 v2 插件。");
            }

            throw new PluginOperationException(
                $"不支持的插件包格式版本：{manifest.FormatVersion}。");
        }

        if (!PluginIdPattern().IsMatch(manifest.PluginId))
        {
            throw new PluginOperationException("插件 ID 格式无效。");
        }

        if (string.IsNullOrWhiteSpace(manifest.DisplayName)
            || manifest.DisplayName.Length > 128)
        {
            throw new PluginOperationException("插件显示名称无效。");
        }

        if (!Version.TryParse(manifest.Version, out _))
        {
            throw new PluginOperationException("插件版本格式无效。");
        }

        if (!Version.TryParse(manifest.MinAppVersion, out var minimumAppVersion))
        {
            throw new PluginOperationException("最低应用版本格式无效。");
        }

        if (_appVersion < minimumAppVersion)
        {
            throw new PluginOperationException(
                $"插件要求 AniMeido {minimumAppVersion} 或更高版本。");
        }

        if (!IsSafePackagePath(manifest.EntryAssembly)
            || manifest.EntryAssembly.Contains('/'))
        {
            throw new PluginOperationException("插件入口程序集必须是包根目录中的安全文件名。");
        }

        if (!manifest.EntryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new PluginOperationException("插件入口程序集必须是 DLL 文件。");
        }

        if (manifest.Files.Count is 0 or > MaximumFileCount)
        {
            throw new PluginOperationException("插件包文件数量无效。");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var normalizedPath = PluginManifest.NormalizePackagePath(file.Path);
            if (!IsSafePackagePath(normalizedPath)
                || string.Equals(normalizedPath, "plugin.json", StringComparison.OrdinalIgnoreCase))
            {
                throw new PluginOperationException($"插件包包含无效路径：{file.Path}");
            }

            if (!paths.Add(normalizedPath))
            {
                throw new PluginOperationException($"插件包包含重复路径：{file.Path}");
            }

            if (!Sha256Pattern().IsMatch(file.Sha256))
            {
                throw new PluginOperationException($"文件哈希格式无效：{file.Path}");
            }
        }

        if (!paths.Contains(PluginManifest.NormalizePackagePath(manifest.EntryAssembly)))
        {
            throw new PluginOperationException("插件入口程序集未包含在文件清单中。");
        }

        ValidateContributions(manifest);
    }

    private static void ValidateContributions(PluginManifest manifest)
    {
        var commandIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var command in manifest.Contributions.Commands)
        {
            if (string.IsNullOrWhiteSpace(command.Id)
                || !command.Id.StartsWith(
                    manifest.PluginId + ".",
                    StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(command.Title)
                || !commandIds.Add(command.Id))
            {
                throw new PluginOperationException("插件命令贡献无效或重复。");
            }
        }

        foreach (var navigation in manifest.Contributions.Navigation)
        {
            if (!commandIds.Contains(navigation.Command))
            {
                throw new PluginOperationException("插件导航贡献引用了未声明的命令。");
            }
        }

        var capabilities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in manifest.Contributions.Capabilities)
        {
            if (!string.Equals(
                    capability,
                    PluginHostProtocol.AnimePlaybackCapability,
                    StringComparison.Ordinal)
                || !capabilities.Add(capability))
            {
                throw new PluginOperationException($"不支持的插件能力：{capability}");
            }
        }

        foreach (var activationEvent in manifest.ActivationEvents)
        {
            var valid = string.Equals(
                    activationEvent,
                    PluginHostProtocol.AnimePlaybackActivationEvent,
                    StringComparison.Ordinal)
                || string.Equals(
                    activationEvent,
                    PluginHostProtocol.StartupFinishedActivationEvent,
                    StringComparison.Ordinal)
                || activationEvent.StartsWith(
                    PluginHostProtocol.CommandActivationPrefix,
                    StringComparison.Ordinal)
                    && commandIds.Contains(
                        activationEvent[PluginHostProtocol.CommandActivationPrefix.Length..]);
            if (!valid)
            {
                throw new PluginOperationException($"不支持的插件激活事件：{activationEvent}");
            }
        }

        foreach (var commandId in commandIds)
        {
            if (!manifest.ActivationEvents.Contains(
                PluginHostProtocol.CommandActivationPrefix + commandId,
                StringComparer.Ordinal))
            {
                throw new PluginOperationException($"插件命令缺少激活事件：{commandId}");
            }
        }

        if (capabilities.Contains(PluginHostProtocol.AnimePlaybackCapability)
            && !manifest.ActivationEvents.Contains(
                PluginHostProtocol.AnimePlaybackActivationEvent,
                StringComparer.Ordinal))
        {
            throw new PluginOperationException("播放能力缺少 onAnimePlayback 激活事件。");
        }
    }

    public async Task VerifyInstalledDirectoryAsync(
        string pluginDirectory,
        CancellationToken cancellationToken = default)
    {
        var manifestPath = Path.Combine(pluginDirectory, "plugin.json");
        PluginManifest manifest;
        try
        {
            manifest = PluginManifest.LoadFromFile(manifestPath)
                ?? throw new PluginOperationException("插件目录缺少 plugin.json。");
        }
        catch (PluginOperationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            throw new PluginOperationException("无法读取插件清单。", ex);
        }

        ValidateManifest(manifest);

        var expectedPaths = manifest.Files
            .Select(file => PluginManifest.NormalizePackagePath(file.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualPaths = Directory
            .EnumerateFiles(pluginDirectory, "*", SearchOption.AllDirectories)
            .Select(path => PluginManifest.NormalizePackagePath(
                Path.GetRelativePath(pluginDirectory, path)))
            .Where(path => !string.Equals(path, "plugin.json", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!actualPaths.SetEquals(expectedPaths))
        {
            throw new PluginOperationException("插件目录文件与清单不一致。");
        }

        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolveSafePath(pluginDirectory, file.Path);
            var actualHash = await ComputeFileHashAsync(path, cancellationToken);
            if (!string.Equals(file.Sha256, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new PluginOperationException($"插件文件校验失败：{file.Path}");
            }
        }
    }

    public static async Task<string> ComputeFileHashAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool IsSafePackagePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || Path.IsPathRooted(path)
            || path.Contains(':')
            || path.Contains('\0'))
        {
            return false;
        }

        var normalized = PluginManifest.NormalizePackagePath(path);
        return normalized.Split('/').All(
            segment => segment.Length > 0
                && segment is not "." and not "..");
    }

    public static string ResolveSafePath(string rootDirectory, string packagePath)
    {
        if (!IsSafePackagePath(packagePath))
        {
            throw new PluginOperationException($"插件包路径不安全：{packagePath}");
        }

        var root = Path.GetFullPath(rootDirectory);
        var candidate = Path.GetFullPath(
            Path.Combine(root, PluginManifest.NormalizePackagePath(packagePath)
                .Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new PluginOperationException($"插件包路径越界：{packagePath}");
        }

        return candidate;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{2,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex PluginIdPattern();

    [GeneratedRegex("^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}

internal sealed class PluginOperationException : Exception
{
    public PluginOperationException(string message)
        : base(message)
    {
    }

    public PluginOperationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
