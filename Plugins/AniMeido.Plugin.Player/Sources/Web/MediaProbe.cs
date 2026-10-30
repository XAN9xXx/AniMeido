using AniMeido.Plugin.Player.Diagnostics;
using System.Net;

namespace AniMeido.Plugin.Player.Sources.Web;

/// <summary>拥有候选媒体的 HTTP 探测、内容识别和失败说明；不管理浏览器与解析预算。</summary>
internal sealed class MediaProbe(HttpClient httpClient, PlaybackDiagnosticRecorder diagnostics)
{
    public async Task<WebResolvedMedia> ValidateAsync(
        WebResolvedMedia media,
        Uri pageUri,
        string sourceId,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, media.Uri);
        HeaderNormalizer.AddToRequest(message, media.Headers, cookies: null);
        var useRangeProbe = ShouldUseRangeProbe(media.Uri);
        if (useRangeProbe)
        {
            message.Headers.Range =
                new System.Net.Http.Headers.RangeHeaderValue(0, 1023);
        }
        else
        {
            message.Headers.Accept.ParseAdd(
                "application/vnd.apple.mpegurl, application/x-mpegURL, */*");
        }

        diagnostics.Record(
            "media-probe",
            "request",
            sourceId,
            media.Uri,
            data: new Dictionary<string, object?>
            {
                ["method"] = "GET",
                ["range"] = useRangeProbe ? "bytes=0-1023" : null,
                ["headerNames"] = string.Join(
                    ",",
                    media.Headers.Keys.Order(StringComparer.OrdinalIgnoreCase)),
            });
        var probeStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        using var response = await httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        diagnostics.Record(
            "media-probe",
            "response",
            sourceId,
            media.Uri,
            data: new Dictionary<string, object?>
            {
                ["status"] = (int)response.StatusCode,
                ["contentType"] =
                    response.Content.Headers.ContentType?.MediaType,
                ["contentLength"] =
                    response.Content.Headers.ContentLength,
                ["acceptRanges"] = string.Join(
                    ",",
                    response.Headers.AcceptRanges),
                ["elapsedMs"] = System.Diagnostics.Stopwatch
                    .GetElapsedTime(probeStarted)
                    .TotalMilliseconds,
            });
        if (response.StatusCode is HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.NotFound)
        {
            var rejectionBody = await ReadResponsePrefixAsync(
                response.Content,
                4096,
                cancellationToken);
            diagnostics.Record(
                "media-probe",
                "rejected-body",
                sourceId,
                media.Uri,
                data: new Dictionary<string, object?>
                {
                    ["status"] = (int)response.StatusCode,
                    ["bytes"] = System.Text.Encoding.UTF8.GetByteCount(
                        rejectionBody),
                },
                rejectionBody);
            throw new SourceResolutionException(
                SourceResolutionFailureKind.MediaRejected,
                DescribeMediaRejection(
                    response.StatusCode,
                    rejectionBody),
                pageUri);
        }

        response.EnsureSuccessStatusCode();
        var contentType =
            response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains(
                "mpegurl",
                StringComparison.OrdinalIgnoreCase)
            || contentType.Contains(
                "octet-stream",
                StringComparison.OrdinalIgnoreCase))
        {
            return media;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        var buffer = new byte[1024];
        var length = await stream.ReadAtLeastAsync(
            buffer, 16, throwOnEndOfStream: false, cancellationToken: cancellationToken);
        var prefix = System.Text.Encoding.UTF8.GetString(buffer, 0, length);
        diagnostics.Record(
            "media-probe",
            "body-prefix",
            sourceId,
            media.Uri,
            data: new Dictionary<string, object?>
            {
                ["bytes"] = length,
            },
            prefix);
        // MIME 不可靠时嗅探实际内容，不能只因 URL 扩展名像视频就放行 HTML。
        var hasMp4Signature = media.Uri.AbsolutePath.EndsWith(
                ".mp4",
                StringComparison.OrdinalIgnoreCase)
            && length >= 16
            && buffer.AsSpan(4, 4).SequenceEqual("ftyp"u8);
        if (prefix.TrimStart().StartsWith(
                "#EXTM3U",
                StringComparison.OrdinalIgnoreCase)
            || hasMp4Signature)
        {
            return media;
        }

        throw new SourceResolutionException(
            SourceResolutionFailureKind.MediaRejected,
            "媒体候选返回了非视频内容，已拒绝交给播放器。",
            pageUri);
    }

    internal static string DescribeMediaRejection(
        HttpStatusCode status,
        string responsePrefix)
    {
        if (responsePrefix.Contains(
                "region has been denied",
                StringComparison.OrdinalIgnoreCase))
        {
            return "媒体 CDN 拒绝当前网络地区。"
                + "请检查 VPN、TUN 或代理分流，并尝试让媒体域名直连。";
        }

        return status switch
        {
            HttpStatusCode.NotFound =>
                "媒体地址已失效（HTTP 404），请切换线路或刷新播放源。",
            HttpStatusCode.Unauthorized =>
                "媒体请求未获授权（HTTP 401），可能需要重新登录源站。",
            _ => $"媒体候选被源站拒绝（HTTP {(int)status}）。",
        };
    }

    private static async Task<string> ReadResponsePrefixAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(
            cancellationToken);
        var buffer = new byte[maximumBytes];
        var length = await stream.ReadAsync(buffer, cancellationToken);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, length);
    }

    internal static bool ShouldUseRangeProbe(Uri mediaUri)
    {
        ArgumentNullException.ThrowIfNull(mediaUri);
        return !mediaUri.AbsolutePath.EndsWith(
            ".m3u8",
            StringComparison.OrdinalIgnoreCase);
    }
}
