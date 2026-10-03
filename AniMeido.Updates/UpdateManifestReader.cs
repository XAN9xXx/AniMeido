using System.Globalization;
using System.Text.Json;

namespace AniMeido.Updates;

public static class UpdateManifestReader
{
    public const int MaxManifestBytes = 64 * 1024;
    public const int MaxSignatureBytes = 1024;
    internal const long MaxPackageBytes = 4L * 1024 * 1024 * 1024;

    public static UpdateManifest Read(ReadOnlySpan<byte> manifestBytes, ReadOnlySpan<byte> signatureText,
        IEnumerable<string> publicKeys, string requestedChannel)
    {
        CheckLengths(manifestBytes.Length, signatureText.Length);
        ArgumentNullException.ThrowIfNull(publicKeys);
        UpdateSignature.Verify(manifestBytes, signatureText, publicKeys);
        return ValidateUnsigned(manifestBytes, requestedChannel);
    }

    public static async Task<UpdateManifest> ReadAsync(Stream manifest, Stream signature,
        IEnumerable<string> publicKeys, string requestedChannel, CancellationToken cancellationToken = default)
    {
        try
        {
            var bytes = await ReadLimitedAsync(manifest, MaxManifestBytes, UpdateErrorKind.ManifestTooLarge,
                cancellationToken).ConfigureAwait(false);
            var signatureBytes = await ReadLimitedAsync(signature, MaxSignatureBytes, UpdateErrorKind.SignatureTooLarge,
                cancellationToken).ConfigureAwait(false);
            return Read(bytes, signatureBytes, publicKeys, requestedChannel);
        }
        catch (IOException ex)
        {
            throw new UpdateException(UpdateErrorKind.FileSystem, "读取更新清单或签名失败。", ex);
        }
    }

    internal static UpdateManifest ValidateUnsigned(ReadOnlySpan<byte> bytes, string requestedChannel)
    {
        CheckLengths(bytes.Length, 0);
        try
        {
            var document = JsonSerializer.Deserialize(bytes, ManifestJsonContext.Default.ManifestDocument);
            if (document is null || document.Schema != 1
                || document.Channel is not ("stable" or "preview") || document.Channel != requestedChannel
                || !SemanticVersion.TryParse(document.Version, out var version)
                || !SemanticVersion.TryParse(document.MinUpdatableFrom, out var minimum)
                || (document.Channel == "stable" && version.IsPrerelease)
                || document.ReleaseDate is null || document.ReleaseDate.Length != 10
                || !DateOnly.TryParseExact(document.ReleaseDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date)
                || document.Notes?.Length > 16000
                || document.Package is not { Urls.Length: >= 1 and <= 8 } package
                || package.Size is < 1 or > MaxPackageBytes
                || package.Sha256 is not { Length: 64 } sha256
                || !sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
            {
                throw InvalidManifest();
            }

            var urls = new Uri[package.Urls.Length];
            for (var i = 0; i < urls.Length; i++)
            {
                if (!Uri.TryCreate(package.Urls[i], UriKind.Absolute, out var uri)
                    || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host))
                {
                    throw InvalidManifest();
                }

                urls[i] = uri;
            }

            return new UpdateManifest(document.Channel, version, date, minimum, document.Notes,
                new UpdatePackage(urls, package.Size, sha256.ToLowerInvariant()));
        }
        catch (JsonException ex)
        {
            throw new UpdateException(UpdateErrorKind.InvalidManifest, "更新清单 JSON 缺少必需字段或字段类型错误。", ex);
        }
    }

    private static void CheckLengths(int manifest, int signature)
    {
        if (manifest > MaxManifestBytes)
        {
            throw new UpdateException(UpdateErrorKind.ManifestTooLarge, "更新清单超过 64 KiB 限制。");
        }

        if (signature > MaxSignatureBytes)
        {
            throw new UpdateException(UpdateErrorKind.SignatureTooLarge, "更新签名文本超过 1 KiB 限制。");
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream stream, int limit, UpdateErrorKind kind,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length,
                limit + 1 - (int)output.Length)), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return output.ToArray();
            }

            output.Write(buffer, 0, count);
            if (output.Length > limit)
            {
                throw new UpdateException(kind, "更新清单或签名文本超过长度限制。");
            }
        }
    }

    private static UpdateException InvalidManifest() =>
        new(UpdateErrorKind.InvalidManifest, "更新清单字段无效或与请求通道不一致。");
}
