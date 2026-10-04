using System.Security.Cryptography;
using AniMeido.Updates;
using Xunit;

namespace AniMeido.Updates.Tests;

public sealed class UpdateTrustedKeysTests
{
    private const string ProductionPublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEOJeoMG4coWpH3j9W3928GGN7ARuQ0VrYv9YvlD6QmghBy9I6YN4AdAAym/9lmfIaonp5VSaGuyE/9rNUEIB10A==";

    [Fact]
    public void ProductionPublicKeyConstructsP256VerifierWithoutPrivateKey()
    {
        Assert.Contains(ProductionPublicKey, UpdateTrustedKeys.PublicKeys);
        using var verifier = ECDsa.Create();
        var bytes = Convert.FromBase64String(ProductionPublicKey);
        verifier.ImportSubjectPublicKeyInfo(bytes, out var read);
        Assert.Equal(bytes.Length, read);
        Assert.Equal(256, verifier.KeySize);
        Assert.Equal(ECCurve.NamedCurves.nistP256.Oid.Value, verifier.ExportParameters(false).Curve.Oid.Value);
        UpdateSignature.RequireP256(verifier);
        Assert.Equal(bytes, verifier.ExportSubjectPublicKeyInfo());
        // 只验证能够导入并调用验签器，不能用生产私钥生成测试签名。
        Assert.False(verifier.VerifyData("test-only"u8, new byte[64], HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }
}
