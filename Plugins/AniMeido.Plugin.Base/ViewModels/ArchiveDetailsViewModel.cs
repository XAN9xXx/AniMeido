using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;

namespace AniMeido.Plugin.Base.ViewModels;

/// <summary>拥有档案详情的选择、查询和发布，不持有控件或页面封面任务。</summary>
internal sealed class ArchiveDetailsViewModel
{
    private readonly Func<int, bool, CancellationToken, Task<ArchiveDetailData>> _loadDetail;
    private readonly Func<int, bool, CancellationToken, Task<ArchiveTimelineData>> _loadTimeline;
    private readonly Func<bool> _isPlaybackAvailable;
    private readonly object _retirementSync = new();
    private readonly List<Task> _retiredStops = [];
    private SelectionRequest? _selection;
    private int _selectionVersion;

    public ArchiveListItem? SelectedArchive { get; private set; }
    public IReadOnlyList<ArchiveTimelineItem> TimelineItems { get; private set; } = [];
    public IReadOnlyList<AnimeScreenshot> DetailScreenshots { get; private set; } = [];
    public bool HasActiveSelection => _selection is not null;

    public event Action<ArchiveListItem?, int>? SelectionChanged;
    public event Action? TimelinePublished;
    public event Action? DetailsPublished;
    public event Action<Exception>? LoadFailed;

    public ArchiveDetailsViewModel(ArchiveQueryService queries, Func<bool> isPlaybackAvailable)
        : this(queries.LoadDetailAsync, queries.LoadTimelineAsync, isPlaybackAvailable)
    {
    }

    internal ArchiveDetailsViewModel(
        Func<int, bool, CancellationToken, Task<ArchiveDetailData>> loadDetail,
        Func<int, bool, CancellationToken, Task<ArchiveTimelineData>> loadTimeline,
        Func<bool> isPlaybackAvailable)
    {
        _loadDetail = loadDetail;
        _loadTimeline = loadTimeline;
        _isPlaybackAvailable = isPlaybackAvailable;
    }

    public Task SelectAsync(ArchiveListItem? selected)
    {
        if (selected is null)
        {
            ClearSelection();
            return Task.CompletedTask;
        }

        return LoadSelectionAsync(selected);
    }

    public void ClearSelection()
    {
        var previous = _selection;
        var version = Interlocked.Increment(ref _selectionVersion);
        _selection = null;
        SelectedArchive = null;
        TimelineItems = [];
        DetailScreenshots = [];
        Retire(previous);
        SelectionChanged?.Invoke(null, version);
    }

    public bool IsCurrentSelection(int version)
        => _selection?.Version == version;

    public void ApplyPlaybackAvailability(bool available)
    {
        if (available)
            return;

        TimelineItems = TimelineItems.Where(item => item.Kind != "观看记录").ToArray();
        TimelinePublished?.Invoke();
    }

    public async Task RefreshTimelineAsync(int animeId)
    {
        var selection = _selection;
        if (selection is null || SelectedArchive?.Archive.AnimeId != animeId)
            return;

        using var operation = selection.Operations.Begin();
        try
        {
            var timeline = await _loadTimeline(animeId, _isPlaybackAvailable(), operation.Token);
            if (!IsCurrentSelection(selection.Version))
                return;

            PublishTimeline(timeline);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
        }
    }

    public Task CancelInflightAsync()
    {
        var previous = _selection;
        _selection = null;
        Interlocked.Increment(ref _selectionVersion);
        Retire(previous);

        Task[] stops;
        lock (_retirementSync)
        {
            _retiredStops.RemoveAll(task => task.IsCompletedSuccessfully);
            stops = _retiredStops.ToArray();
        }
        return DrainRetiredAsync(stops);
    }

    private async Task LoadSelectionAsync(ArchiveListItem selected)
    {
        var previous = _selection;
        var selection = new SelectionRequest(Interlocked.Increment(ref _selectionVersion));
        _selection = selection;
        SelectedArchive = selected;
        TimelineItems = [];
        DetailScreenshots = [];

        // 页面回调可能立即换选中项；先持有本次查询，停止才不会抢先释放它的取消资源。
        using var operation = selection.Operations.Begin();
        Retire(previous);
        SelectionChanged?.Invoke(selected, selection.Version);
        if (!IsCurrentSelection(selection.Version))
            return;

        try
        {
            var detail = await _loadDetail(
                selected.Archive.AnimeId, _isPlaybackAvailable(), operation.Token);
            if (!IsCurrentSelection(selection.Version))
                return;

            PublishTimeline(detail.Timeline);
            if (!IsCurrentSelection(selection.Version))
                return;

            DetailScreenshots = detail.Screenshots.Where(item => item.FileExists).ToArray();
            DetailsPublished?.Invoke();
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
        {
            if (IsCurrentSelection(selection.Version))
                LoadFailed?.Invoke(ex);
        }
    }

    private void PublishTimeline(ArchiveTimelineData timeline)
    {
        // 查询开始后 Player 仍可能启停；投影采用发布时的可用性，不恢复已经隐藏的观看记录。
        TimelineItems = ArchiveBrowse.BuildTimeline(
            timeline.Entries, timeline.WatchHistory, timeline.TrackingChanges, _isPlaybackAvailable());
        TimelinePublished?.Invoke();
    }

    private void Retire(SelectionRequest? selection)
    {
        if (selection is null)
            return;

        var stopping = selection.Operations.StopAsync().AsTask();
        lock (_retirementSync)
        {
            // 成功的旧任务不保留历史；失败留给离页时的最终等待方。
            _retiredStops.RemoveAll(task => task.IsCompletedSuccessfully);
            _retiredStops.Add(stopping);
        }
    }

    private async Task DrainRetiredAsync(Task[] stops)
    {
        try
        {
            await Task.WhenAll(stops);
        }
        finally
        {
            lock (_retirementSync)
            {
                foreach (var task in stops)
                    _retiredStops.Remove(task);
            }
        }
    }

    private sealed class SelectionRequest(int version)
    {
        public int Version { get; } = version;
        public ServiceOperationLifetime Operations { get; } = new();
    }
}
