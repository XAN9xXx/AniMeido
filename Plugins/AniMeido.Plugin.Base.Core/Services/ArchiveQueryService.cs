using AniMeido.Plugin.Base.Models;

namespace AniMeido.Plugin.Base.Services;

/// <summary>
/// 档案浏览的完整查询入口。SQLite 的异步 API 仍可能同步执行，
/// 因此一次面板/详情查询整体在后台执行，页面只接收结果，不逐条调度 SQL。
/// 不参与写入准入，也不把其他页面、网络请求或界面更新串入同一队列。
/// </summary>
public sealed class ArchiveQueryService(ArchiveService archive)
{
    public Task<IReadOnlyList<ArchiveListItem>> LoadArchivesAsync(CancellationToken ct = default)
        => ReadAsync(() => archive.GetArchiveListAsync(ct), ct);

    public Task<ArchiveStatistics> LoadStatisticsAsync(CancellationToken ct = default)
        => ReadAsync(() => archive.GetStatisticsAsync(cancellationToken: ct), ct);

    public Task<ArchiveReviewData> LoadReviewAsync(int year, CancellationToken ct = default)
        => ReadAsync(async () => new ArchiveReviewData(
            year,
            await archive.GetStatisticsAsync(year, ct).ConfigureAwait(false),
            await archive.GetReviewCoversAsync(year, ct).ConfigureAwait(false),
            await archive.GetReviewMomentsAsync(year, cancellationToken: ct).ConfigureAwait(false),
            await archive.GetReviewScreenshotsAsync(year, ct).ConfigureAwait(false)), ct);

    public Task<ArchiveScreenshotData> LoadScreenshotsAsync(CancellationToken ct = default)
        => ReadAsync(async () => new ArchiveScreenshotData(
            await archive.GetScreenshotsAsync(cancellationToken: ct).ConfigureAwait(false),
            await archive.GetAllScreenshotTagsAsync(ct).ConfigureAwait(false)), ct);

    public Task<ArchiveTimelineData> LoadTimelineAsync(
        int animeId, bool includePlayback, CancellationToken ct = default)
        => ReadAsync(() => ReadTimelineAsync(animeId, includePlayback, ct), ct);

    public Task<ArchiveDetailData> LoadDetailAsync(
        int animeId, bool includePlayback, CancellationToken ct = default)
        => ReadAsync(async () => new ArchiveDetailData(
            await ReadTimelineAsync(animeId, includePlayback, ct).ConfigureAwait(false),
            await archive.GetScreenshotsAsync(animeId, ct).ConfigureAwait(false)), ct);

    private async Task<ArchiveTimelineData> ReadTimelineAsync(
        int animeId, bool includePlayback, CancellationToken ct)
        => new(
            await archive.GetEntriesAsync(animeId, ct).ConfigureAwait(false),
            includePlayback ? await archive.GetWatchHistoryAsync(animeId, ct).ConfigureAwait(false) : [],
            await archive.GetTrackingChangesAsync(animeId, ct).ConfigureAwait(false));

    internal static Task<T> ReadAsync<T>(Func<Task<T>> read, CancellationToken ct)
        => Task.Run(read, ct);
}
