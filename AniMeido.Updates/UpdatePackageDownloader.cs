using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace AniMeido.Updates;

public static class UpdatePackageDownloader
{
    public static async Task<string> DownloadAsync(UpdatePackage package, string downloadDirectory,
        HttpClient httpClient, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(httpClient);
        try
        {
            return await DownloadCoreAsync(package, downloadDirectory, httpClient, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException(UpdateErrorKind.FileSystem, "无法读写更新包目录。", ex);
        }
    }

    private static async Task<string> DownloadCoreAsync(UpdatePackage package, string directory,
        HttpClient client, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        var finalPath = Path.Combine(directory, package.Sha256 + ".zip");
        var partialPath = finalPath + ".partial";
        if (File.Exists(finalPath))
        {
            if (await MatchesAsync(finalPath, package, cancellationToken).ConfigureAwait(false))
            {
                progress?.Report(package.Size);
                return finalPath;
            }

            File.Delete(finalPath);
        }

        if (File.Exists(partialPath) && new FileInfo(partialPath).Length > package.Size)
        {
            File.Delete(partialPath);
        }

        if (File.Exists(partialPath) && new FileInfo(partialPath).Length == package.Size)
        {
            if (await MatchesAsync(partialPath, package, cancellationToken).ConfigureAwait(false))
            {
                File.Move(partialPath, finalPath);
                progress?.Report(package.Size);
                return finalPath;
            }

            File.Delete(partialPath);
        }

        long reported = 0;
        void Report(long value)
        {
            // 镜像重试或服务器忽略 Range 时，不让界面进度倒退。
            if (value > reported)
            {
                reported = value;
                progress?.Report(value);
            }
        }

        UpdateException? lastError = null;
        foreach (var url in package.Urls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await DownloadFromAsync(url, partialPath, package.Size, client, Report, cancellationToken)
                    .ConfigureAwait(false);
                if (!await MatchesAsync(partialPath, package, cancellationToken).ConfigureAwait(false))
                {
                    throw new UpdateException(UpdateErrorKind.HashMismatch, "更新包大小或 SHA-256 校验失败。");
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(partialPath, finalPath);
                Report(package.Size);
                return finalPath;
            }
            catch (UpdateException ex) when (ex.Kind is UpdateErrorKind.HashMismatch
                or UpdateErrorKind.PackageTooLarge or UpdateErrorKind.Network)
            {
                if (ex.Kind is UpdateErrorKind.HashMismatch or UpdateErrorKind.PackageTooLarge)
                {
                    File.Delete(partialPath);
                }

                lastError = ex;
            }
            catch (HttpRequestException ex)
            {
                lastError = new UpdateException(UpdateErrorKind.Network, "下载更新包的网络请求失败。", ex);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = new UpdateException(UpdateErrorKind.Network, "下载更新包的网络请求超时或被中断。", ex);
            }
        }

        throw lastError ?? new UpdateException(UpdateErrorKind.Network, "没有可用的更新包下载地址。");
    }

    private static async Task DownloadFromAsync(Uri url, string partialPath, long size, HttpClient client,
        Action<long> report, CancellationToken cancellationToken)
    {
        var offset = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var finalUri = response.RequestMessage?.RequestUri ?? request.RequestUri;
        if (finalUri?.Scheme != Uri.UriSchemeHttps)
        {
            throw new UpdateException(UpdateErrorKind.Network, "更新包请求不得降级到非 HTTPS 地址。");
        }

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (range?.Unit != "bytes" || range.From != offset || range.Length != size
                || range.To is null || range.To < range.From || range.To >= size)
            {
                throw new UpdateException(UpdateErrorKind.Network, "更新包续传响应的 Content-Range 无效。");
            }
        }
        else if (response.StatusCode == HttpStatusCode.OK)
        {
            offset = 0;
        }
        else
        {
            throw new UpdateException(UpdateErrorKind.Network, $"更新包服务器返回错误状态码：{(int)response.StatusCode}。");
        }

        await using var output = new FileStream(partialPath, offset == 0 ? FileMode.Create : FileMode.Append,
            FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        Stream responseStream;
        try
        {
            responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new UpdateException(UpdateErrorKind.Network, "无法打开更新包响应流。", ex);
        }

        await using var input = responseStream;
        report(offset);
        var buffer = new byte[81920];
        long written = offset;
        long received = 0;
        while (true)
        {
            int count;
            try
            {
                count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new UpdateException(UpdateErrorKind.Network, "读取更新包响应流失败。", ex);
            }

            if (count == 0)
            {
                break;
            }

            if (count > size - written)
            {
                throw new UpdateException(UpdateErrorKind.PackageTooLarge, "更新包数据超过清单声明的大小，已中止下载。");
            }

            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            written += count;
            received += count;
            report(written);
        }

        var contentRange = response.Content.Headers.ContentRange;
        if (response.StatusCode == HttpStatusCode.PartialContent
            && received != contentRange!.To!.Value - contentRange.From!.Value + 1)
        {
            throw new UpdateException(UpdateErrorKind.Network, "续传响应实际长度与 Content-Range 不一致。");
        }
    }

    private static async Task<bool> MatchesAsync(string path, UpdatePackage package,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != package.Size)
        {
            return false;
        }

        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase);
    }
}
