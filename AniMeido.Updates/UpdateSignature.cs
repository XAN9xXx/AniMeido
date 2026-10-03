using System.Security.Cryptography;
using System.Text;

namespace AniMeido.Updates;

internal static class UpdateSignature
{
    internal const int SignatureLength = 64;

    internal static byte[] Sign(ReadOnlySpan<byte> data, ECDsa key)
    {
        RequireP256(key);
        return Encoding.ASCII.GetBytes(Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
    }

    internal static void Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signatureText,
        IEnumerable<string> publicKeys)
    {
        // 只接受 Base64 单行 ASCII，可带一个末尾 LF 或 CRLF；不接受内嵌空白。
        var length = signatureText.Length;
        if (length > 0 && signatureText[length - 1] == (byte)'\n')
        {
            length--;
            if (length > 0 && signatureText[length - 1] == (byte)'\r')
            {
                length--;
            }
        }

        var line = signatureText[..length];
        if (line.IsEmpty || !IsBase64Line(line))
        {
            throw InvalidSignature();
        }

        Span<byte> signature = stackalloc byte[SignatureLength];
        if (!Convert.TryFromBase64String(Encoding.ASCII.GetString(line), signature, out var written)
            || written != SignatureLength)
        {
            throw InvalidSignature();
        }

        Exception? keyError = null;
        foreach (var publicKey in publicKeys)
        {
            try
            {
                using var key = ECDsa.Create();
                var bytes = Convert.FromBase64String(publicKey);
                key.ImportSubjectPublicKeyInfo(bytes, out var read);
                if (read != bytes.Length)
                {
                    continue;
                }

                RequireP256(key);
                if (key.VerifyData(data, signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
            {
                // 损坏或非 P-256 的候选密钥不能阻止下一把有效密钥；保留失败原因。
                keyError = ex;
            }
        }

        throw new UpdateException(UpdateErrorKind.InvalidSignature, "更新清单签名无效，不能信任其内容。", keyError);
    }

    private static bool IsBase64Line(ReadOnlySpan<byte> line)
    {
        foreach (var c in line)
        {
            if (c is not (>= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'
                or >= (byte)'0' and <= (byte)'9' or (byte)'+' or (byte)'/' or (byte)'='))
            {
                return false;
            }
        }

        return true;
    }

    internal static void RequireP256(ECDsa key)
    {
        if (key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
        {
            throw new CryptographicException("更新签名必须使用 ECDSA P-256 密钥。");
        }
    }

    private static UpdateException InvalidSignature() =>
        new(UpdateErrorKind.InvalidSignature, "更新清单签名无效，不能信任其内容。");
}
