#if UPDATE_TEST_SOURCE
using System.Net;
using System.Net.Http.Headers;

namespace AniMeido.App.Services;

/// <summary>仅开发验收编译：替换更新字节来源，保留正式验签、校验和 Range 续传路径。</summary>
internal sealed class UpdateTestSourceHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        if (uri is null || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "dl.animeido.com"
            || !uri.IsDefaultPort || !uri.AbsolutePath.StartsWith("/updates/", StringComparison.Ordinal))
            return base.SendAsync(request, cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = Environment.GetEnvironmentVariable("ANIMEIDO_UPDATE_TEST_ROOT");
            if (string.IsNullOrWhiteSpace(root))
                throw new IOException("未设置 ANIMEIDO_UPDATE_TEST_ROOT。");
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var relative = Uri.UnescapeDataString(uri.AbsolutePath["/updates/".Length..]);
            var segments = relative.Split('/');
            if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."
                || segment.Contains('\\') || segment.Contains(':')))
                throw new IOException("本地更新源路径无效。");
            var path = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("本地更新源路径超出测试目录。");
            for (var entry = path; entry is not null; entry = Path.GetDirectoryName(entry))
            {
                if (File.Exists(entry) || Directory.Exists(entry))
                {
                    if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("本地更新源不允许符号链接或 junction。");
                }
            }
            if (!File.Exists(path))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
            var range = request.Headers.Range;
            if (range is not null && (range.Unit != "bytes" || range.Ranges.Count != 1
                || range.Ranges.First().From is null || range.Ranges.First().To is not null))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
                    { RequestMessage = request });
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var length = stream.Length;
            var offset = range?.Ranges.SingleOrDefault()?.From ?? 0;
            if (range is not null && offset >= length)
            {
                stream.Dispose();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
                    { RequestMessage = request });
            }
            stream.Position = offset;
            var content = new StreamContent(stream);
            content.Headers.ContentLength = length - offset;
            if (range is not null)
                content.Headers.ContentRange = new ContentRangeHeaderValue(offset, length - 1, length);
            return Task.FromResult(new HttpResponseMessage(range is null
                ? HttpStatusCode.OK : HttpStatusCode.PartialContent) { RequestMessage = request, Content = content });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Serilog.Log.Warning(ex, "读取本地更新测试源失败");
            throw new HttpRequestException("读取本地更新测试源失败。", ex);
        }
    }
}
#endif
