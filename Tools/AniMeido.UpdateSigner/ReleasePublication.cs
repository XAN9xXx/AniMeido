using System.Security.Cryptography;
using AniMeido.Updates;

namespace AniMeido.UpdateSigner;

internal static class ReleasePublication
{
    // An identical rerun may repair the detached signature after an interrupted upload.
    // It must not replace already published versioned binaries.
    internal static bool ShouldPublish(string directory, string versionText, string? currentPath,
        IReadOnlyList<string> trustedKeys)
    {
        var version = SemanticVersion.Parse(versionText);
        var channel = version.IsPrerelease ? "preview" : "stable";
        var file = Path.Combine(directory, "release.json");
        var bytes = ReadBounded(file, UpdateManifestReader.MaxManifestBytes);
        var manifest = UpdateManifestReader.Read(bytes, ReadBounded(file + ".sig", UpdateManifestReader.MaxSignatureBytes),
            trustedKeys, channel);
        var zip = $"AniMeido-{version}-win-x64.zip";
        var url = $"https://dl.animeido.com/updates/win-x64/{channel}/{zip}";
        if (manifest.Version.ToString() != versionText || !manifest.Package.Urls.Any(u => u.AbsoluteUri == url))
        {
            throw new UpdateException(UpdateErrorKind.InvalidManifest, "签名清单的版本或下载地址与发布目标不同。");
        }

        var installer = new FileInfo(Path.Combine(directory, $"AniMeido_setup_{version}.exe"));
        if (!installer.Exists || installer.Length == 0)
        {
            throw new UpdateException(UpdateErrorKind.RequiredFileMissing, "缺少此版本的安装包或安装包为空。");
        }

        using var package = File.OpenRead(Path.Combine(directory, zip));
        if (package.Length != manifest.Package.Size
            || !Convert.ToHexString(SHA256.HashData(package)).Equals(manifest.Package.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException(UpdateErrorKind.HashMismatch, "ZIP 大小或摘要与已签名清单不一致。");
        }

        if (currentPath is null) return true;

        // Read from authenticated R2, not the CDN. The remote signature can already belong
        // to the next release if publication stopped between the two discovery writes.
        var current = ReadBounded(currentPath, UpdateManifestReader.MaxManifestBytes);
        if (bytes.AsSpan().SequenceEqual(current)) return false;
        var previous = UpdateManifestReader.ValidateUnsigned(current, channel);
        if (version.CompareTo(previous.Version) <= 0)
        {
            throw new UpdateException(UpdateErrorKind.InvalidVersion, $"R2 已有 {previous.Version}，拒绝降级或替换同版本清单。");
        }

        return true;
    }

    private static byte[] ReadBounded(string path, int maximum)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > maximum) throw new UpdateException(UpdateErrorKind.InvalidManifest, "发布文件超出长度限制。");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
