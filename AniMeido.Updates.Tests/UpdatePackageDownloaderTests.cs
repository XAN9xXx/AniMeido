using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AniMeido.Updates;
using Xunit;

namespace AniMeido.Updates.Tests;

public sealed class UpdatePackageDownloaderTests
{
    private static readonly byte[] PackageBytes = Encoding.UTF8.GetBytes("complete update package bytes");

    [Fact]
    public async Task FullDownloadAndMonotonicProgress()
    {
        using var temp = new TemporaryDirectory();
        var data = Enumerable.Range(0, 200000).Select(i => (byte)i).ToArray();
        var package = TestData.Verified(TestData.Document(data)).Package;
        using var handler = new FakeHandler((request, _) =>
        {
            Assert.Null(request.Headers.Range);
            return Task.FromResult(TestData.Response(data));
        });
        using var client = new HttpClient(handler);
        var values = new List<long>();
        var path = await UpdatePackageDownloader.DownloadAsync(package, temp.Root, client, new InlineProgress(values.Add));
        Assert.Equal(data, File.ReadAllBytes(path));
        Assert.Equal(1, handler.Calls);
        Assert.Equal(package.Size, values[^1]);
        Assert.True(values.SequenceEqual(values.OrderBy(v => v)));
        Assert.True(values.All(v => v <= package.Size));
        Assert.False(File.Exists(path + ".partial"));
        Assert.Equal(package.Sha256 + ".zip", Path.GetFileName(path));
    }

    [Fact]
    public async Task PartialContentResumes()
    {
        using var temp = new TemporaryDirectory();
        var package = TestData.Verified(TestData.Document(PackageBytes)).Package;
        var partial = temp.PathFor(package.Sha256 + ".zip.partial");
        File.WriteAllBytes(partial, PackageBytes[..5]);
        using var handler = new FakeHandler((request, _) =>
        {
            Assert.Equal(5, request.Headers.Range!.Ranges.Single().From);
            return Task.FromResult(Resume(PackageBytes, 5));
        });
        using var client = new HttpClient(handler);
        var path = await UpdatePackageDownloader.DownloadAsync(package, temp.Root, client);
        Assert.Equal(PackageBytes, File.ReadAllBytes(path));
        Assert.False(File.Exists(partial));
    }

    [Fact]
    public async Task ServerIgnoresRangeAndRestarts()
    {
        using var temp = new TemporaryDirectory();
        var package = TestData.Verified(TestData.Document(PackageBytes)).Package;
        File.WriteAllBytes(temp.PathFor(package.Sha256 + ".zip.partial"), Encoding.UTF8.GetBytes("bad prefix"));
        using var handler = new FakeHandler((request, _) =>
        {
            Assert.NotNull(request.Headers.Range);
            return Task.FromResult(TestData.Response(PackageBytes));
        });
        using var client = new HttpClient(handler);
        var path = await UpdatePackageDownloader.DownloadAsync(package, temp.Root, client);
        Assert.Equal(PackageBytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(0, 29, "bytes")]
    [InlineData(5, 30, "bytes")]
    [InlineData(5, 29, "items")]
    public async Task IncorrectContentRangeRejected(long from, long total, string unit)
    {
        using var temp = new TemporaryDirectory();
        var package = TestData.Verified(TestData.Document(PackageBytes)).Package;
        Assert.Equal(29, package.Size);
        var partial = temp.PathFor(package.Sha256 + ".zip.partial");
        File.WriteAllBytes(partial, PackageBytes[..5]);
        using var handler = new FakeHandler((_, _) =>
        {
            var response = TestData.Response(PackageBytes[5..], HttpStatusCode.PartialContent);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, total - 1, total) { Unit = unit };
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        await TestData.ErrorAsync(UpdateErrorKind.Network,
            () => UpdatePackageDownloader.DownloadAsync(package, temp.Root, client));
        Assert.Equal(PackageBytes[..5], File.ReadAllBytes(partial));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task OversizedPartialDeletedBeforeRequest()
    {
        using var temp = new TemporaryDirectory();
        var package = TestData.Verified(TestData.Document(PackageBytes)).Package;
        File.WriteAllBytes(temp.PathFor(package.Sha256 + ".zip.partial"), new byte[package.Size + 1]);
        using var handler = new FakeHandler((request, _) =>
        {
            Assert.Null(request.Headers.Range);
            return Task.FromResult(TestData.Response(PackageBytes));
        });
        using var client = new HttpClient(handler);
        await UpdatePackageDownloader.DownloadAsync(package, temp.Root, client);
    }

    [Fact]
    public async Task OversizedResponseAbortsAndDeletesPartial()
    {
        using var temp = new TemporaryDirectory();
        var package = TestData.Verified(TestData.Document(PackageBytes)).Package;
        using var handler = new FakeHandler((_, _) =>
            Task.FromResult(TestData.Response(new byte[PackageBytes.Length + 1])));
        using var client = new HttpClient(handler);
        await TestData.ErrorAsync(UpdateErrorKind.PackageTooLarge,
            () => UpdatePackageDownloader.DownloadAsync(package, temp.Root, client));
        Assert.Equal(2, handler.Calls);
        Assert.False(File.Exists(temp.PathFor(package.Sha256 + ".zip.partial")));
        Assert.False(File.Exists(temp.PathFor(package.Sha256 + ".zip")));
    }

    [Fact]
    public async Task BadHashDeletedAndSecondMirrorSucceeds()
    {
        using var temp = new TemporaryDirectory();
        var package = TestData.Verified(TestData.Document(PackageBytes)).Package;
        var calls = 0;
        using var handler = new FakeHandler((request, _) =>
        {
            calls++;
            if (calls == 1)
            {
                Assert.Equal("first.example", request.RequestUri!.Host);
                return Task.FromResult(TestData.Response(new byte[PackageBytes.Length]));
            }

            Assert.Equal("second.example", request.RequestUri!.Host);
            Assert.False(File.Exists(temp.PathFor(package.Sha256 + ".zip.partial")));
            Assert.Null(request.Headers.Range);
            return Task.FromResult(TestData.Response(PackageBytes));
        });
        using var client = new HttpClient(handler);
        var values = new List<long>();
        var path = await UpdatePackageDownloader.DownloadAsync(package, temp.Root, client, new InlineProgress(values.Add));
        Assert.Equal(PackageBytes, File.ReadAllBytes(path));
        Assert.Equal(2, calls);
        Assert.True(values.SequenceEqual(values.OrderBy(v => v)));
    }

    [Fact]
    public async Task AllMirrorsFailWithLastNetworkError()
    {
        using var temp = new TemporaryDirectory();
        var package = TestData.Verified(TestData.Document(PackageBytes)).Package;
        var calls = 0;
        using var handler = new FakeHandler((_, _) => Task.FromResult(
            TestData.Response([], ++calls == 1 ? HttpStatusCode.InternalServerError : HttpStatusCode.NotFound)));
        using var client = new HttpClient(handler);
        var exception = await TestData.ErrorAsync(UpdateErrorKind.Network,
            () => UpdatePackageDownloader.DownloadAsync(package, temp.Root, client));
        Assert.Contains("404", exception.Message);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task NetworkExceptionFallsBackAndRetainsCause()
    {
        using var temp = new TemporaryDirectory();
        var package = TestData.Verified(TestData.Document(PackageBytes)).Package;
        using var handler = new FakeHandler((request, _) => throw new HttpRequestException(request.RequestUri!.Host));
        using var client = new HttpClient(handler);
        var error = await TestData.ErrorAsync(UpdateErrorKind.Network,
            () => UpdatePackageDownloader.DownloadAsync(package, temp.Root, client));
        Assert.Equal("second.example", error.InnerException!.Message);
    }

    [Fact]
    public async Task CorrectFinalFileDoesNotSendRequest()
    {
        using var temp = new TemporaryDirectory();
        var package = TestData.Verified(TestData.Document(PackageBytes)).Package;
        var final = temp.PathFor(package.Sha256 + ".zip");
        File.WriteAllBytes(final, PackageBytes);
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException("不应请求"));
        using var client = new HttpClient(handler);
        Assert.Equal(final, await UpdatePackageDownloader.DownloadAsync(package, temp.Root, client));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidFinalFileIsReplaced(bool sameSize)
    {
        using var temp = new TemporaryDirectory();
        var package = TestData.Verified(TestData.Document(PackageBytes)).Package;
        var final = temp.PathFor(package.Sha256 + ".zip");
        File.WriteAllBytes(final, sameSize ? new byte[PackageBytes.Length] : []);
        using var handler = new FakeHandler((_, _) => Task.FromResult(TestData.Response(PackageBytes)));
        using var client = new HttpClient(handler);
        Assert.Equal(final, await UpdatePackageDownloader.DownloadAsync(package, temp.Root, client));
        Assert.Equal(PackageBytes, File.ReadAllBytes(final));
    }

    [Fact]
    public async Task CompletedPartialIsVerifiedWithoutRequest()
    {
        using var temp = new TemporaryDirectory();
        var package = TestData.Verified(TestData.Document(PackageBytes)).Package;
        File.WriteAllBytes(temp.PathFor(package.Sha256 + ".zip.partial"), PackageBytes);
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException("不应请求"));
        using var client = new HttpClient(handler);
        var path = await UpdatePackageDownloader.DownloadAsync(package, temp.Root, client);
        Assert.Equal(PackageBytes, File.ReadAllBytes(path));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task CancellationKeepsPartialForNextResume()
    {
        using var temp = new TemporaryDirectory();
        var bytes = Enumerable.Range(0, 200000).Select(i => (byte)i).ToArray();
        var package = TestData.Verified(TestData.Document(bytes)).Package;
        using var cancellation = new CancellationTokenSource();
        using var firstHandler = new FakeHandler((_, _) => Task.FromResult(TestData.Response(bytes)));
        using var firstClient = new HttpClient(firstHandler);
        var progress = new InlineProgress(value =>
        {
            if (value > 0)
            {
                cancellation.Cancel();
            }
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            UpdatePackageDownloader.DownloadAsync(package, temp.Root, firstClient, progress, cancellation.Token));
        var partial = temp.PathFor(package.Sha256 + ".zip.partial");
        Assert.True(File.Exists(partial));
        var length = new FileInfo(partial).Length;
        Assert.InRange(length, 1, bytes.Length - 1);
        using var secondHandler = new FakeHandler((request, _) =>
        {
            Assert.Equal(length, request.Headers.Range!.Ranges.Single().From);
            return Task.FromResult(Resume(bytes, length));
        });
        using var secondClient = new HttpClient(secondHandler);
        var path = await UpdatePackageDownloader.DownloadAsync(package, temp.Root, secondClient);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task FinalHttpsRequestAcceptedAndDowngradeRejected()
    {
        using var temp = new TemporaryDirectory();
        var package = TestData.Verified(TestData.Document(PackageBytes)).Package;
        using var handler = new FakeHandler((_, _) =>
        {
            var response = TestData.Response(PackageBytes);
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://bad.example/package.zip");
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        await TestData.ErrorAsync(UpdateErrorKind.Network,
            () => UpdatePackageDownloader.DownloadAsync(package, temp.Root, client));
        Assert.False(File.Exists(temp.PathFor(package.Sha256 + ".zip.partial")));
        using var httpsHandler = new FakeHandler((_, _) =>
        {
            var response = TestData.Response(PackageBytes);
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://mirror.example/package.zip");
            return Task.FromResult(response);
        });
        using var httpsClient = new HttpClient(httpsHandler);
        await UpdatePackageDownloader.DownloadAsync(package, temp.Root, httpsClient);
    }

    [Fact]
    public async Task NetworkReadFailureKeepsPrefixForMirrorResume()
    {
        using var temp = new TemporaryDirectory();
        var package = TestData.Verified(TestData.Document(PackageBytes)).Package;
        var calls = 0;
        using var handler = new FakeHandler((request, _) =>
        {
            if (++calls == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new FailingAfterPrefixStream(PackageBytes[..5]))
                });
            }

            Assert.Equal(5, request.Headers.Range!.Ranges.Single().From);
            return Task.FromResult(Resume(PackageBytes, 5));
        });
        using var client = new HttpClient(handler);
        var path = await UpdatePackageDownloader.DownloadAsync(package, temp.Root, client);
        Assert.Equal(PackageBytes, File.ReadAllBytes(path));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ShortResponsesDeletedAndLastHashErrorReturned()
    {
        using var temp = new TemporaryDirectory();
        var package = TestData.Verified(TestData.Document(PackageBytes)).Package;
        using var handler = new FakeHandler((_, _) => Task.FromResult(TestData.Response(PackageBytes[..2])));
        using var client = new HttpClient(handler);
        await TestData.ErrorAsync(UpdateErrorKind.HashMismatch,
            () => UpdatePackageDownloader.DownloadAsync(package, temp.Root, client));
        Assert.False(File.Exists(temp.PathFor(package.Sha256 + ".zip.partial")));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task ProgressDoesNotRegressWhenRangeIsIgnoredByMirror()
    {
        using var temp = new TemporaryDirectory();
        var bytes = new byte[200000];
        var package = TestData.Verified(TestData.Document(bytes)).Package;
        File.WriteAllBytes(temp.PathFor(package.Sha256 + ".zip.partial"), bytes[..100000]);
        using var handler = new FakeHandler((request, _) =>
        {
            Assert.Equal(100000, request.Headers.Range!.Ranges.Single().From);
            return Task.FromResult(TestData.Response(bytes));
        });
        using var client = new HttpClient(handler);
        var values = new List<long>();
        await UpdatePackageDownloader.DownloadAsync(package, temp.Root, client, new InlineProgress(values.Add));
        Assert.True(values.SequenceEqual(values.OrderBy(v => v)));
        Assert.Equal(bytes.Length, values[^1]);
    }

    private sealed class FailingAfterPrefixStream(byte[] prefix) : MemoryStream(prefix)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Position >= Length)
            {
                throw new IOException("模拟响应流中断");
            }

            return ValueTask.FromResult(base.Read(buffer.Span));
        }
    }

    private static HttpResponseMessage Resume(byte[] bytes, long offset)
    {
        var response = TestData.Response(bytes[(int)offset..], HttpStatusCode.PartialContent);
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, bytes.Length - 1, bytes.Length);
        return response;
    }
}
