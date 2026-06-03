using AniMeido.App.Models;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;

namespace AniMeido.App.Services;

/// <summary>
/// 插件清单签名验证器。
/// 使用内嵌 RSA 公钥验证 plugin.json 签名的合法性。
///
/// 公钥生成（首次使用）：
///   dotnet script 或 PowerShell:
///     $rsa = [System.Security.Cryptography.RSA]::Create(2048)
///     $priv = [Convert]::ToBase64String($rsa.ExportRSAPrivateKey())
///     $pub  = [Convert]::ToBase64String($rsa.ExportRSAPublicKey())
///     Write-Host "Private: $priv"
///     Write-Host "Public:  $pub"
/// 私钥安全保存，公钥填入下方的 EmbeddedPublicKey。
///
/// 签名生成工具见项目根目录的 Tools/sign-plugin.ps1。
/// </summary>
internal static class PluginSignatureVerifier
{
    /// <summary>
    /// 内嵌 RSA 公钥（Base64 编码的 SubjectPublicKeyInfo）。
    /// 首次使用请运行 Tools/sign-plugin.ps1 -GenerateKeyPair 生成，
    /// 将公钥粘贴至此，私钥安全保管。
    /// </summary>
    private const string EmbeddedPublicKey = "";

    /// <summary>是否已配置公钥（未配置时跳过签名验证）。</summary>
    public static bool IsConfigured => !string.IsNullOrEmpty(EmbeddedPublicKey);

    /// <summary>
    /// 验证插件清单的签名。
    /// 公钥未配置时：Debug 放行，Release 拒绝。
    /// </summary>
    /// <param name="manifest">插件清单。</param>
    /// <param name="dllPath">插件主 DLL 路径（用于验证哈希）。</param>
    /// <param name="logger">日志记录器。</param>
    /// <returns>验证通过返回 true；否则返回 false。</returns>
    public static bool Verify(PluginManifest manifest, string dllPath, Microsoft.Extensions.Logging.ILogger logger)
    {
        if (!IsConfigured)
        {
#if DEBUG
            logger.LogWarning("DEBUG 模式：公钥未配置，跳过插件签名验证");
            return true;
#else
            logger.LogError("插件签名公钥未配置，拒绝加载外部插件: {DllPath}", dllPath);
            return false;
#endif
        }

        // 1. 验证 DLL 哈希
        if (!VerifyDllHash(manifest, dllPath, logger))
            return false;

        // 2. 验证清单签名
        if (!VerifySignature(manifest, logger))
            return false;

        return true;
    }

    /// <summary>
    /// 计算文件的 SHA-256 哈希（十六进制小写）。
    /// </summary>
    public static string ComputeFileHash(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hashBytes = sha256.ComputeHash(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static bool VerifyDllHash(PluginManifest manifest, string dllPath, Microsoft.Extensions.Logging.ILogger logger)
    {
        if (!File.Exists(dllPath))
        {
            logger.LogError("插件 DLL 不存在: {DllPath}", dllPath);
            return false;
        }

        var actualHash = ComputeFileHash(dllPath);
        if (!string.Equals(manifest.Hash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError(
                "插件 DLL 哈希不匹配: 清单中为 {ManifestHash}, 实际为 {ActualHash}",
                manifest.Hash, actualHash);
            return false;
        }

        return true;
    }

    private static bool VerifySignature(PluginManifest manifest, Microsoft.Extensions.Logging.ILogger logger)
    {
        if (string.IsNullOrEmpty(manifest.Signature))
        {
            logger.LogError("插件清单缺少签名字段");
            return false;
        }

        try
        {
            var publicKeyBytes = Convert.FromBase64String(EmbeddedPublicKey);
            using var rsa = RSA.Create();
            rsa.ImportRSAPublicKey(publicKeyBytes, out _);

            var payloadBytes = Encoding.UTF8.GetBytes(manifest.ManifestPayload);
            var signatureBytes = Convert.FromBase64String(manifest.Signature);

            return rsa.VerifyData(payloadBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
#pragma warning disable CA1031 // 签名验证异常视为验证失败
        catch (Exception ex)
        {
            logger.LogError(ex, "插件签名验证过程发生异常");
            return false;
        }
#pragma warning restore CA1031
    }
}
