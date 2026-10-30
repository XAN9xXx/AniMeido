using AniMeido.Plugin.Base.Models;
using System.Net;
using System.Text;

namespace AniMeido.Plugin.Base.Services;

/// <summary>年度回顾导出：查询、HTML 呈现与文件替换由一次后台操作完成，选择器仍属于页面。</summary>
public sealed class ArchiveReviewExporter(ArchiveService archive)
{
    public Task ExportAsync(string path, int year, bool includePlayback, CancellationToken ct = default)
        => Task.Run(async () =>
        {
            var statistics = await archive.GetStatisticsAsync(year, ct).ConfigureAwait(false);
            var moments = await archive.GetReviewMomentsAsync(year, 8, ct).ConfigureAwait(false);
            var screenshots = includePlayback
                ? (await archive.GetScreenshotsAsync(year: year, cancellationToken: ct).ConfigureAwait(false))
                    .Where(item => item.FileExists).Take(12).ToArray()
                : [];
            var html = await BuildReviewHtmlAsync(year, statistics, moments, screenshots, includePlayback, ct)
                .ConfigureAwait(false);
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporaryPath, html, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }, ct);

    private static async Task<string> BuildReviewHtmlAsync(
        int year,
        ArchiveStatistics statistics,
        IReadOnlyList<AnnualReviewMoment> moments,
        IReadOnlyList<AnimeScreenshot> screenshots,
        bool playbackAvailable,
        CancellationToken ct)
    {
        var title = WebUtility.HtmlEncode($"{year} 年 AniMeido 年度回顾");
        var tags = WebUtility.HtmlEncode(string.Join(
            "、",
            statistics.TagCounts.Take(8).Select(item => item.Key)));
        var momentCards = new StringBuilder();
        foreach (var moment in moments)
        {
            var date = WebUtility.HtmlEncode(moment.DateText);
            var anime = WebUtility.HtmlEncode(moment.AnimeTitle);
            var body = WebUtility.HtmlEncode(moment.Entry.Body);
            momentCards.Append(
                $"<article class=\"moment\"><small>{date} · {anime}</small><p>{body}</p></article>");
        }
        var images = new StringBuilder();
        foreach (var screenshot in screenshots)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(screenshot.FilePath, ct).ConfigureAwait(false);
                var caption = WebUtility.HtmlEncode(
                    screenshot.ContextNote.Length > 0
                        ? screenshot.ContextNote
                        : screenshot.AnimeTitle ?? "截图");
                images.Append(
                    $"<figure><img src=\"data:image/png;base64,{Convert.ToBase64String(bytes)}\" alt=\"截图\"><figcaption>{caption}</figcaption></figure>");
            }
            catch (IOException)
            {
                // A screenshot can disappear after it was listed.
            }
        }
        var duration = $"{statistics.EstimatedWatchMinutes / 60} 小时 {statistics.EstimatedWatchMinutes % 60} 分钟";
        var playbackCards = playbackAvailable
            ? $"<div class=\"card\"><strong>{statistics.ScreenshotCount}</strong><small>截图记录</small></div>"
                + $"<div class=\"card\"><strong>{duration}</strong><small>估算观看时长</small></div>"
            : string.Empty;
        var gallery = playbackAvailable
            ? $"<h2>这一年的截图</h2><div class=\"gallery\">{images}</div>"
            : string.Empty;
        return $$"""
            <!doctype html><html lang="zh-CN"><head><meta charset="utf-8">
            <title>{{title}}</title><style>
            body{font-family:"Segoe UI","Microsoft YaHei",sans-serif;
            max-width:960px;margin:48px auto;padding:0 24px;
            color:#f2f5f9;background:#202225;line-height:1.6}
            h1{font-size:2.5rem;color:#91ccf5}h2{margin-top:36px}
            .cards{display:grid;grid-template-columns:repeat(2,1fr);gap:12px}
            .card,.moment,figure{background:#303338;border-radius:12px;padding:20px}
            .gallery{display:grid;grid-template-columns:repeat(2,1fr);gap:12px}
            figure{margin:0}img{width:100%;aspect-ratio:16/9;object-fit:cover;
            border-radius:8px}figcaption,small{color:#c8d3dc}
            strong{font-size:2rem;color:#91ccf5;display:block}
            .moment{margin-bottom:10px}.moment p{white-space:pre-wrap;margin-bottom:0}
            </style></head><body><h1>{{title}}</h1>
            <div class="cards">
            <div class="card"><strong>{{statistics.ArchiveCount}}</strong><small>新增档案</small></div>
            <div class="card"><strong>{{statistics.EntryCount}}</strong><small>写下感想</small></div>
            {{playbackCards}}
            </div><h2>这一年的感想</h2>{{momentCards}}
            {{gallery}}
            <h2>年度新增档案的个人标签</h2><p>{{tags}}</p></body></html>
            """;
    }

}
