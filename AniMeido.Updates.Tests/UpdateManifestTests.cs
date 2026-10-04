using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AniMeido.Updates;
using Xunit;

namespace AniMeido.Updates.Tests;

public sealed class UpdateManifestTests
{
    [Fact]
    public void SignatureRoundtripAndMultipleKeys()
    {
        using var key = TestData.Key();
        using var other = TestData.Key();
        var bytes = TestData.Bytes();
        var signature = UpdateSignature.Sign(bytes, key);
        Assert.Equal(64, Convert.FromBase64String(Encoding.ASCII.GetString(signature)).Length);
        var manifest = UpdateManifestReader.Read(bytes, signature, [TestData.Public(other), TestData.Public(key)], "stable");
        Assert.Equal("2.3.0", manifest.Version.ToString());
        Assert.Equal("stable", manifest.Channel);
        Assert.Equal(new DateOnly(2026, 10, 3), manifest.ReleaseDate);
        Assert.Equal(1, manifest.Schema);
        Assert.Equal(64, manifest.Package.Sha256.Length);
    }

    [Fact]
    public void MalformedAndNonP256KeysCannotAuthorizeButDoNotBlockValidKey()
    {
        using var key = TestData.Key();
        using var wrongCurve = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var bytes = TestData.Bytes();
        var signature = UpdateSignature.Sign(bytes, key);
        var wrongPublic = TestData.Public(wrongCurve);
        TestData.Error(UpdateErrorKind.InvalidSignature,
            () => UpdateManifestReader.Read(bytes, signature, [wrongPublic], "stable"));
        var error = TestData.Error(UpdateErrorKind.InvalidSignature,
            () => UpdateManifestReader.Read(bytes, signature, ["invalid key"], "stable"));
        Assert.NotNull(error.InnerException);
        UpdateManifestReader.Read(bytes, signature, ["invalid key", wrongPublic, TestData.Public(key)], "stable");
    }

    [Fact]
    public void EmbeddedWhitespaceAndExtraNewlinesRejected()
    {
        using var key = TestData.Key();
        var bytes = TestData.Bytes();
        var signature = Encoding.ASCII.GetString(UpdateSignature.Sign(bytes, key));
        foreach (var text in new[] { " " + signature, signature.Insert(4, "\n"), signature + "\n\n", signature + "\r" })
        {
            TestData.Error(UpdateErrorKind.InvalidSignature,
                () => UpdateManifestReader.Read(bytes, Encoding.ASCII.GetBytes(text), [TestData.Public(key)], "stable"));
        }
    }

    [Fact]
    public async Task SignatureStreamAndExactManifestLimit()
    {
        using var key = TestData.Key();
        var bytes = TestData.Bytes();
        var padded = new byte[UpdateManifestReader.MaxManifestBytes];
        bytes.CopyTo(padded, 0);
        Array.Fill(padded, (byte)' ', bytes.Length, padded.Length - bytes.Length);
        using var manifest = new MemoryStream(padded);
        using var signature = new MemoryStream(UpdateSignature.Sign(padded, key));
        var result = await UpdateManifestReader.ReadAsync(manifest, signature, [TestData.Public(key)], "stable");
        Assert.Equal("2.3.0", result.Version.ToString());
        using var validManifest = new MemoryStream(bytes);
        using var oversizedSignature = new MemoryStream(new byte[UpdateManifestReader.MaxSignatureBytes + 1]);
        await TestData.ErrorAsync(UpdateErrorKind.SignatureTooLarge,
            () => UpdateManifestReader.ReadAsync(validManifest, oversizedSignature, [TestData.Public(key)], "stable"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void SignatureAllowsSingleTrailingNewline(string suffix)
    {
        using var key = TestData.Key();
        var bytes = TestData.Bytes();
        var line = Encoding.ASCII.GetString(UpdateSignature.Sign(bytes, key)) + suffix;
        UpdateManifestReader.Read(bytes, Encoding.ASCII.GetBytes(line), [TestData.Public(key)], "stable");
    }

    [Fact]
    public void EveryByteTamperingIsRejected()
    {
        using var key = TestData.Key();
        var bytes = TestData.Bytes();
        var signature = UpdateSignature.Sign(bytes, key);
        var publicKeys = new[] { TestData.Public(key) };
        for (var i = 0; i < bytes.Length; i++)
        {
            var tampered = (byte[])bytes.Clone();
            tampered[i] ^= 1;
            TestData.Error(UpdateErrorKind.InvalidSignature,
                () => UpdateManifestReader.Read(tampered, signature, publicKeys, "stable"));
        }
    }

    [Fact]
    public void WrongKeyEmptyKeysAndDerRejected()
    {
        using var key = TestData.Key();
        using var other = TestData.Key();
        var bytes = TestData.Bytes();
        var signature = UpdateSignature.Sign(bytes, key);
        TestData.Error(UpdateErrorKind.InvalidSignature,
            () => UpdateManifestReader.Read(bytes, signature, [TestData.Public(other)], "stable"));
        TestData.Error(UpdateErrorKind.InvalidSignature,
            () => UpdateManifestReader.Read(bytes, signature, [], "stable"));
        TestData.Error(UpdateErrorKind.InvalidSignature,
            () => UpdateManifestReader.Read(bytes, signature, UpdateTrustedKeys.PublicKeys, "stable"));
        var der = key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        TestData.Error(UpdateErrorKind.InvalidSignature,
            () => UpdateManifestReader.Read(bytes, Encoding.ASCII.GetBytes(Convert.ToBase64String(der)), [TestData.Public(key)], "stable"));
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("")]
    [InlineData("AA==")]
    [InlineData(" ")]
    [InlineData("中文")]
    public void InvalidSignatureText(string signature)
    {
        using var key = TestData.Key();
        TestData.Error(UpdateErrorKind.InvalidSignature, () => UpdateManifestReader.Read(TestData.Bytes(),
            Encoding.UTF8.GetBytes(signature), [TestData.Public(key)], "stable"));
    }

    [Theory]
    [InlineData(63)]
    [InlineData(65)]
    public void WrongSignatureLength(int length)
    {
        using var key = TestData.Key();
        var text = Encoding.ASCII.GetBytes(Convert.ToBase64String(new byte[length]));
        TestData.Error(UpdateErrorKind.InvalidSignature,
            () => UpdateManifestReader.Read(TestData.Bytes(), text, [TestData.Public(key)], "stable"));
    }

    [Fact]
    public void InvalidJsonIsNotParsedBeforeSignature()
    {
        TestData.Error(UpdateErrorKind.InvalidSignature,
            () => UpdateManifestReader.Read(Encoding.UTF8.GetBytes("not json"), [], [], "stable"));
    }

    [Fact]
    public void LengthChecksPrecedeAnyKeyEnumeration()
    {
        static IEnumerable<string> MustNotEnumerate() =>
            Enumerable.Range(0, 1).Select<int, string>(_ => throw new InvalidOperationException("不应验签"));

        TestData.Error(UpdateErrorKind.ManifestTooLarge, () => UpdateManifestReader.Read(
            new byte[UpdateManifestReader.MaxManifestBytes + 1], [], MustNotEnumerate(), "stable"));
        TestData.Error(UpdateErrorKind.SignatureTooLarge, () => UpdateManifestReader.Read(
            TestData.Bytes(), new byte[UpdateManifestReader.MaxSignatureBytes + 1], MustNotEnumerate(), "stable"));
    }

    [Fact]
    public async Task StreamLengthChecksAreBounded()
    {
        using var manifest = new CountingStream(UpdateManifestReader.MaxManifestBytes + 10000);
        using var signature = new MemoryStream();
        await TestData.ErrorAsync(UpdateErrorKind.ManifestTooLarge,
            () => UpdateManifestReader.ReadAsync(manifest, signature, [], "stable"));
        Assert.Equal(UpdateManifestReader.MaxManifestBytes + 1, manifest.BytesRead);
    }

    public static IEnumerable<object[]> InvalidFields()
    {
        yield return ["schema", "2"];
        yield return ["schema", "\"1\""];
        yield return ["channel", "\"nightly\""];
        yield return ["channel", "\"preview\""];
        yield return ["version", "\"2.3.0-preview.1\""];
        yield return ["version", "\"v2.3.0\""];
        yield return ["version", "23"];
        yield return ["minUpdatableFrom", "\"1.2\""];
        yield return ["releaseDate", "\"2026-2-03\""];
        yield return ["releaseDate", "\"2026-02-30\""];
        yield return ["releaseDate", "\"2026-10-03T00:00:00Z\""];
        yield return ["notes", "123"];
        yield return ["notes", "\"" + new string('a', 16001) + "\""];
        yield return ["package", "null"];
        yield return ["package.urls", "[]"];
        yield return ["package.urls", "[\"http://example.com/a\"]"];
        yield return ["package.urls", "[\"/relative.zip\"]"];
        yield return ["package.urls", "[\"https:///\"]"];
        yield return ["package.urls", "[null]"];
        yield return ["package.urls", "\"https://example.com/a\""];
        yield return ["package.urls", "[" + string.Join(",", Enumerable.Repeat("\"https://example.com/a\"", 9)) + "]"];
        yield return ["package.size", "0"];
        yield return ["package.size", "-1"];
        yield return ["package.size", "4294967297"];
        yield return ["package.size", "\"1\""];
        yield return ["package.size", "1.5"];
        yield return ["package.sha256", "\"abcd\""];
        yield return ["package.sha256", "\"" + new string('g', 64) + "\""];
        yield return ["package.sha256", "null"];
    }

    [Theory]
    [MemberData(nameof(InvalidFields))]
    public void InvalidFieldsAreRejected(string path, string json)
    {
        var node = JsonNode.Parse(TestData.Bytes())!;
        Set(node, path, JsonNode.Parse(json));
        AssertRejected(Encoding.UTF8.GetBytes(node.ToJsonString()));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("channel")]
    [InlineData("version")]
    [InlineData("releaseDate")]
    [InlineData("minUpdatableFrom")]
    [InlineData("notes")]
    [InlineData("package")]
    [InlineData("package.urls")]
    [InlineData("package.size")]
    [InlineData("package.sha256")]
    public void MissingRequiredFieldIsRejected(string path)
    {
        var node = JsonNode.Parse(TestData.Bytes())!;
        var (parent, property) = Locate(node, path);
        parent.AsObject().Remove(property);
        AssertRejected(Encoding.UTF8.GetBytes(node.ToJsonString()));
    }

    [Theory]
    [InlineData("channel")]
    [InlineData("version")]
    [InlineData("releaseDate")]
    [InlineData("minUpdatableFrom")]
    [InlineData("package.urls")]
    [InlineData("package.size")]
    public void NullRequiredValueIsRejected(string path)
    {
        var node = JsonNode.Parse(TestData.Bytes())!;
        Set(node, path, null);
        AssertRejected(Encoding.UTF8.GetBytes(node.ToJsonString()));
    }

    [Fact]
    public void NamesAreCaseSensitiveAndUnknownFieldsAreIgnored()
    {
        var node = JsonNode.Parse(TestData.Bytes())!;
        node["futureField"] = new JsonObject { ["whatever"] = true };
        node["package"]!["futureField"] = 2;
        using var key = TestData.Key();
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString());
        UpdateManifestReader.Read(bytes, UpdateSignature.Sign(bytes, key), [TestData.Public(key)], "stable");
        node.AsObject().Remove("version");
        node["Version"] = "2.3.0";
        AssertRejected(Encoding.UTF8.GetBytes(node.ToJsonString()));
    }

    [Fact]
    public void PreviewNullNotesAndMaximumFieldsAreAccepted()
    {
        var document = TestData.Document();
        document.Channel = "preview";
        document.Version = "2.4.0-preview.1+commit";
        document.Notes = null;
        document.Package!.Size = 4294967296;
        document.Package.Urls = Enumerable.Repeat("https://example.com/a", 8).ToArray();
        Assert.True(TestData.Verified(document).Version.IsPrerelease);
        document.Notes = new string('a', 16000);
        TestData.Verified(document);
        using var key = TestData.Key();
        var bytes = TestData.Bytes(document);
        TestData.Error(UpdateErrorKind.InvalidManifest,
            () => UpdateManifestReader.Read(bytes, UpdateSignature.Sign(bytes, key), [TestData.Public(key)], "stable"));
    }

    [Theory]
    [InlineData("2.3.0", "2.2.0", "2.3.0", UpdateDecision.NoUpdate)]
    [InlineData("2.3.0+new", "2.2.0", "2.3.0+old", UpdateDecision.NoUpdate)]
    [InlineData("2.3.0", "2.2.0", "2.4.0-preview.1", UpdateDecision.NoUpdate)]
    [InlineData("2.3.0", "2.2.0", "2.2.0", UpdateDecision.UpdateAvailable)]
    [InlineData("2.3.0", "2.2.0", "2.1.2", UpdateDecision.ReinstallRequired)]
    [InlineData("2.4.0-preview.2", "2.2.0", "2.4.0-preview.1", UpdateDecision.UpdateAvailable)]
    [InlineData("2.3.0", "2.4.0", "2.3.0", UpdateDecision.NoUpdate)]
    public void DecisionBranches(string version, string minimum, string current, UpdateDecision expected)
    {
        var document = TestData.Document();
        document.Version = version;
        document.MinUpdatableFrom = minimum;
        document.Channel = SemanticVersion.Parse(version).IsPrerelease ? "preview" : "stable";
        Assert.Equal(expected, UpdatePolicy.Evaluate(TestData.Verified(document), SemanticVersion.Parse(current)));
    }

    private static void AssertRejected(byte[] bytes)
    {
        using var key = TestData.Key();
        var signature = UpdateSignature.Sign(bytes, key);
        TestData.Error(UpdateErrorKind.InvalidManifest,
            () => UpdateManifestReader.Read(bytes, signature, [TestData.Public(key)], "stable"));
    }

    private static (JsonNode Parent, string Property) Locate(JsonNode node, string path)
    {
        var parts = path.Split('.');
        return parts.Length == 1 ? (node, parts[0]) : (node[parts[0]]!, parts[1]);
    }

    private static void Set(JsonNode node, string path, JsonNode? value)
    {
        var (parent, property) = Locate(node, path);
        parent[property] = value;
    }

    private sealed class CountingStream(int length) : MemoryStream(new byte[length])
    {
        internal int BytesRead { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = base.Read(buffer.Span);
            BytesRead += count;
            return ValueTask.FromResult(count);
        }
    }
}
