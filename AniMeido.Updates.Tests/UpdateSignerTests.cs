using System.Security.Cryptography;
using System.Text;
using AniMeido.UpdateSigner;
using Xunit;

namespace AniMeido.Updates.Tests;

public sealed class UpdateSignerTests
{
    [Theory]
    [InlineData("stable", "2.3.0")]
    [InlineData("preview", "2.4.0-preview.1")]
    public void KeygenManifestSignVerifyRoundtrip(string channel, string version)
    {
        using var temp = new TemporaryDirectory();
        var privatePath = temp.PathFor("private.pem");
        var publicPath = temp.PathFor("public.txt");
        Assert.Equal(0, Run("keygen", "--private", privatePath, "--public", publicPath).Code);
        Assert.StartsWith("-----BEGIN PRIVATE KEY-----", File.ReadAllText(privatePath));
        Assert.DoesNotContain("\n", File.ReadAllText(publicPath));
        using (var key = ECDsa.Create())
        {
            key.ImportFromPem(File.ReadAllText(privatePath));
            Assert.Equal(256, key.KeySize);
            Assert.Equal(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), File.ReadAllText(publicPath));
        }

        var zip = TestData.Zip(temp, ("AniMeido.App.exe", Encoding.UTF8.GetBytes("exe"), 0));
        var manifest = temp.PathFor("release.json");
        var notes = temp.PathFor("notes.txt");
        File.WriteAllText(notes, "更新说明");
        Assert.Equal(0, Run("manifest", "--zip", zip, "--version", version, "--channel", channel,
            "--url", "https://first.example/a.zip", "--url", "https://second.example/a.zip",
            "--release-date", "2026-10-03", "--min-updatable-from", "2.2.0",
            "--notes-file", notes, "--out", manifest).Code);
        Assert.Equal(0, Run("sign", "--private", privatePath, "--file", manifest).Code);
        Assert.Equal(64, Convert.FromBase64String(File.ReadAllText(manifest + ".sig")).Length);
        var verify = Run("verify", "--public", publicPath, "--file", manifest, "--channel", channel);
        Assert.Equal(0, verify.Code);
        Assert.Contains(version, verify.Output);
        Assert.Contains(channel, verify.Output);
        Assert.Equal("", verify.Error);
        Assert.Equal(0, Run("verify", "--public", File.ReadAllText(publicPath), "--file", manifest, "--channel", channel).Code);

        File.AppendAllText(manifest, " ");
        var tampered = Run("verify", "--public", publicPath, "--file", manifest, "--channel", channel);
        Assert.Equal(1, tampered.Code);
        Assert.NotEmpty(tampered.Error);
        Assert.Empty(tampered.Output);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void KeygenRefusesEitherExistingTarget(bool privateExists)
    {
        using var temp = new TemporaryDirectory();
        var privatePath = temp.PathFor("private.pem");
        var publicPath = temp.PathFor("public.txt");
        var existing = privateExists ? privatePath : publicPath;
        File.WriteAllText(existing, "sentinel");
        Assert.Equal(1, Run("keygen", "--private", privatePath, "--public", publicPath).Code);
        Assert.Equal("sentinel", File.ReadAllText(existing));
        Assert.False(File.Exists(privateExists ? publicPath : privatePath));
    }

    [Fact]
    public void SignRefusesOverwriteUnlessExplicitlyRequested()
    {
        using var temp = new TemporaryDirectory();
        var privatePath = temp.PathFor("private.pem");
        var publicPath = temp.PathFor("public.txt");
        Assert.Equal(0, Run("keygen", "--private", privatePath, "--public", publicPath).Code);
        var manifest = temp.PathFor("release.json");
        File.WriteAllBytes(manifest, TestData.Bytes());
        Assert.Equal(0, Run("sign", "--private", privatePath, "--file", manifest).Code);
        var oldSignature = File.ReadAllBytes(manifest + ".sig");
        Assert.Equal(1, Run("sign", "--private", privatePath, "--file", manifest).Code);
        Assert.Equal(oldSignature, File.ReadAllBytes(manifest + ".sig"));
        Assert.Equal(0, Run("sign", "--private", privatePath, "--file", manifest, "--overwrite").Code);
        Assert.Equal(0, Run("verify", "--public", publicPath, "--file", manifest).Code);
    }

    [Fact]
    public void ManifestUsesSharedValidationAndRefusesOverwrite()
    {
        using var temp = new TemporaryDirectory();
        var zip = TestData.Zip(temp, ("file", [], 0));
        var manifest = temp.PathFor("release.json");
        string[] Args(string version, string url) =>
        [
            "manifest", "--zip", zip, "--version", version, "--channel", "stable",
            "--url", url, "--release-date", "2026-10-03", "--out", manifest
        ];
        Assert.Equal(1, Run(Args("2.3.0-preview.1", "https://example.com/a")).Code);
        Assert.False(File.Exists(manifest));
        Assert.Equal(1, Run(Args("2.3.0", "http://example.com/a")).Code);
        Assert.False(File.Exists(manifest));
        Assert.Equal(0, Run(Args("2.3.0", "https://example.com/a")).Code);
        var original = File.ReadAllBytes(manifest);
        Assert.Equal(1, Run(Args("2.4.0", "https://example.com/a")).Code);
        Assert.Equal(original, File.ReadAllBytes(manifest));
    }

    [Fact]
    public void OversizedNotesRefusedBeforeWritingManifest()
    {
        using var temp = new TemporaryDirectory();
        var notes = temp.PathFor("notes.txt");
        File.WriteAllText(notes, new string('a', 16001));
        var zip = TestData.Zip(temp, ("file", [], 0));
        var output = temp.PathFor("release.json");
        var result = Run("manifest", "--zip", zip, "--version", "2.3.0", "--channel", "stable",
            "--url", "https://example.com/a", "--release-date", "2026-10-03", "--notes-file", notes, "--out", output);
        Assert.Equal(1, result.Code);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void InvalidPrivateKeyIsValidationFailureAndNeverPrinted()
    {
        using var temp = new TemporaryDirectory();
        var privatePath = temp.PathFor("private.pem");
        const string secret = "THIS_IS_NOT_A_VALID_PRIVATE_KEY";
        File.WriteAllText(privatePath, secret);
        var file = temp.PathFor("release.json");
        File.WriteAllBytes(file, TestData.Bytes());
        var result = Run("sign", "--private", privatePath, "--file", file);
        Assert.Equal(1, result.Code);
        Assert.DoesNotContain(secret, result.Output + result.Error);
        Assert.False(File.Exists(file + ".sig"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("keygen")]
    [InlineData("sign|--private")]
    [InlineData("sign|--private|key.pem|--file|release.json|--bad")]
    [InlineData("verify|--public|key|--file|file|--channel|nightly")]
    [InlineData("verify|--public|key|--file|file|--file|other")]
    [InlineData("verify|--public|key|--file|file|--overwrite")]
    public void ParameterErrorsReturnTwo(string arguments)
    {
        var result = Run(arguments.Length == 0 ? [] : arguments.Split('|'));
        Assert.Equal(2, result.Code);
        Assert.NotEmpty(result.Error);
        Assert.Empty(result.Output);
    }

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = SignerCommands.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }
}
