using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AniMeido.Updates;
using Xunit;

namespace AniMeido.Updates.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "AniMeidoUpdatesTests_" + Guid.NewGuid().ToString("N"));
    internal TemporaryDirectory() => Directory.CreateDirectory(Root);
    internal string PathFor(string name) => Path.Combine(Root, name);

    public void Dispose()
    {
        var full = Path.GetFullPath(Root);
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith("AniMeidoUpdatesTests_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("拒绝删除临时目录以外的路径。");
        }

        if (Directory.Exists(full))
        {
            Directory.Delete(full, true);
        }
    }
}

internal sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    : HttpMessageHandler
{
    internal int Calls { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        Assert.Equal("https", request.RequestUri!.Scheme);
        return await send(request, cancellationToken);
    }
}

internal sealed class InlineProgress(Action<long> report) : IProgress<long>
{
    public void Report(long value) => report(value);
}

internal static class TestData
{
    internal static ManifestDocument Document(byte[]? package = null)
    {
        package ??= Encoding.UTF8.GetBytes("update package");
        return new ManifestDocument
        {
            Schema = 1,
            Channel = "stable",
            Version = "2.3.0",
            ReleaseDate = "2026-10-03",
            MinUpdatableFrom = "2.2.0",
            Notes = "",
            Package = new PackageDocument
            {
                Urls = ["https://first.example/package.zip", "https://second.example/package.zip"],
                Size = package.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(package))
            }
        };
    }

    internal static byte[] Bytes(ManifestDocument? document = null) =>
        JsonSerializer.SerializeToUtf8Bytes(document ?? Document(), ManifestJsonContext.Default.ManifestDocument);

    internal static ECDsa Key() => ECDsa.Create(ECCurve.NamedCurves.nistP256);
    internal static string Public(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    internal static UpdateManifest Verified(ManifestDocument? document = null)
    {
        using var key = Key();
        var bytes = Bytes(document);
        return UpdateManifestReader.Read(bytes, UpdateSignature.Sign(bytes, key), [Public(key)], document?.Channel ?? "stable");
    }

    internal static UpdateException Error(UpdateErrorKind kind, Action action)
    {
        var exception = Assert.Throws<UpdateException>(action);
        Assert.Equal(kind, exception.Kind);
        return exception;
    }

    internal static async Task<UpdateException> ErrorAsync(UpdateErrorKind kind, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<UpdateException>(action);
        Assert.Equal(kind, exception.Kind);
        return exception;
    }

    internal static HttpResponseMessage Response(byte[] bytes, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new ByteArrayContent(bytes) };

    internal static string Zip(TemporaryDirectory temp, params (string Name, byte[] Bytes, int Attributes)[] entries)
    {
        var path = temp.PathFor(Guid.NewGuid().ToString("N") + ".zip");
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = zip.CreateEntry(item.Name, CompressionLevel.SmallestSize);
            entry.ExternalAttributes = item.Attributes;
            using var stream = entry.Open();
            stream.Write(item.Bytes);
        }

        return path;
    }
}
