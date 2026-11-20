using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;

namespace AniMeido.Plugin.Base.ViewModels;

/// <summary>拥有档案四面板的查询结果、加载状态与失效规则，不持有页面或控件。</summary>
internal sealed class ArchivePanelsViewModel
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<ArchiveListItem>>> _loadArchives;
    private readonly Func<CancellationToken, Task<ArchiveStatistics>> _loadStatistics;
    private readonly Func<int, CancellationToken, Task<ArchiveReviewData>> _loadReview;
    private readonly Func<CancellationToken, Task<ArchiveScreenshotData>> _loadScreenshots;
    private readonly Dictionary<ArchivePanelKind, PanelLoadState> _states =
        Enum.GetValues<ArchivePanelKind>().ToDictionary(kind => kind, _ => new PanelLoadState());
    private readonly Dictionary<string, IReadOnlyList<string>> _screenshotTags = new(StringComparer.Ordinal);

    public ArchivePanelKind ActivePanel { get; private set; } = ArchivePanelKind.Archives;
    public IReadOnlyList<ArchiveListItem> Archives { get; private set; } = [];
    public ArchiveStatistics? Statistics { get; private set; }
    public ArchiveReviewData? Review { get; private set; }
    public IReadOnlyList<AnimeScreenshot> Screenshots { get; private set; } = [];
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ScreenshotTags => _screenshotTags;

    public event Action<ArchivePanelKind>? PanelPublished;
    public event Action<ArchivePanelKind, Exception?>? PanelLoadCompleted;

    public ArchivePanelsViewModel(ArchiveQueryService queries)
        : this(
            queries.LoadArchivesAsync,
            queries.LoadStatisticsAsync,
            queries.LoadReviewAsync,
            queries.LoadScreenshotsAsync)
    {
    }

    internal ArchivePanelsViewModel(
        Func<CancellationToken, Task<IReadOnlyList<ArchiveListItem>>> loadArchives,
        Func<CancellationToken, Task<ArchiveStatistics>> loadStatistics,
        Func<int, CancellationToken, Task<ArchiveReviewData>> loadReview,
        Func<CancellationToken, Task<ArchiveScreenshotData>> loadScreenshots)
    {
        _loadArchives = loadArchives;
        _loadStatistics = loadStatistics;
        _loadReview = loadReview;
        _loadScreenshots = loadScreenshots;
    }

    public void SelectPanel(ArchivePanelKind panel)
    {
        if (ActivePanel != panel && _states[ActivePanel].IsLoading)
            _states[ActivePanel].CancelInflight();
        ActivePanel = panel;
    }

    public void Invalidate(params ArchivePanelKind[] panels)
    {
        foreach (var panel in panels)
            _states[panel].Invalidate();
    }

    public void InvalidateAll() => Invalidate(_states.Keys.ToArray());

    public void CancelInflight()
    {
        foreach (var state in _states.Values)
            state.CancelInflight();
    }

    public void ReplaceScreenshotTags(string screenshotId, IReadOnlyList<string> tags)
        => _screenshotTags[screenshotId] = tags;

    public async Task<bool> EnsureActiveAsync(int reviewYear, CancellationToken lifetimeCancellation)
    {
        var panel = ActivePanel;
        var state = _states[panel];
        var generation = state.Generation;
        try
        {
            var load = state.EnsureAsync(
                (requestGeneration, cancellationToken) => LoadPanelAsync(
                    panel, reviewYear, requestGeneration, cancellationToken),
                lifetimeCancellation);
            generation = state.Generation;
            await load;
            if (IsCurrent(panel, generation, lifetimeCancellation))
            {
                PanelLoadCompleted?.Invoke(panel, null);
                return true;
            }
        }
        catch (OperationCanceledException) when (
            lifetimeCancellation.IsCancellationRequested
            || generation != state.Generation
            || !state.IsCurrent(generation, lifetimeCancellation))
        {
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or SqliteException)
        {
            if (IsCurrent(panel, generation, lifetimeCancellation))
                PanelLoadCompleted?.Invoke(panel, ex);
        }

        return false;
    }

    private bool IsCurrent(ArchivePanelKind panel, int generation, CancellationToken cancellationToken)
        => ActivePanel == panel && _states[panel].IsCurrent(generation, cancellationToken);

    private async Task LoadPanelAsync(
        ArchivePanelKind panel,
        int reviewYear,
        int generation,
        CancellationToken cancellationToken)
    {
        switch (panel)
        {
            case ArchivePanelKind.Archives:
                var archives = await _loadArchives(cancellationToken);
                if (!IsCurrent(panel, generation, cancellationToken))
                    return;
                Archives = archives;
                break;
            case ArchivePanelKind.Statistics:
                var statistics = await _loadStatistics(cancellationToken);
                if (!IsCurrent(panel, generation, cancellationToken))
                    return;
                Statistics = statistics;
                break;
            case ArchivePanelKind.Review:
                var review = await _loadReview(reviewYear, cancellationToken);
                if (!IsCurrent(panel, generation, cancellationToken))
                    return;
                Review = review;
                break;
            case ArchivePanelKind.Screenshots:
                var gallery = await _loadScreenshots(cancellationToken);
                if (!IsCurrent(panel, generation, cancellationToken))
                    return;
                Screenshots = gallery.Screenshots;
                _screenshotTags.Clear();
                foreach (var screenshot in Screenshots)
                    _screenshotTags[screenshot.ScreenshotId] =
                        gallery.Tags.GetValueOrDefault(screenshot.ScreenshotId) ?? [];
                break;
        }
        PanelPublished?.Invoke(panel);
    }
}
