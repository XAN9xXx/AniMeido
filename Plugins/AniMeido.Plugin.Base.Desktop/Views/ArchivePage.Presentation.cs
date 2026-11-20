using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Globalization;
using System.Text.Json;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class ArchivePage
{
    private ArchiveListItem? _presentedArchive;
    private int _archiveReadingGeneration;
    private void OnArchiveCoverLoaded(object sender, RoutedEventArgs e)
        => _ = ConfigureArchiveCoverAsync(sender as Image);

    private void OnReviewCoverLoaded(object sender, RoutedEventArgs e)
        => _ = ConfigureArchiveCoverAsync(sender as Image, 200);

    private void OnReviewCoverDataContextChanged(
        FrameworkElement sender,
        DataContextChangedEventArgs args)
    {
        _ = args;
        _ = ConfigureArchiveCoverAsync(sender as Image, 200);
    }

    private void OnArchiveCoverDataContextChanged(
        FrameworkElement sender,
        DataContextChangedEventArgs args)
    {
        _ = args;
        _ = ConfigureArchiveCoverAsync(sender as Image);
    }

    internal static int? GetArchiveCoverAnimeId(object? context)
        => context switch
        {
            ArchiveListItem item => item.Archive.AnimeId,
            ArchiveReviewCover cover => cover.AnimeId,
            AnnualReviewMoment moment => moment.AnimeId,
            _ => null,
        };

    private async Task ConfigureArchiveCoverAsync(
        Image? image,
        int decodeWidth = 56)
    {
        var animeId = GetArchiveCoverAnimeId(image?.DataContext);
        if (image is null || animeId is null || _pageLifetime is null)
        {
            if (image is not null)
                ManagedImageLoader.Cancel(image);
            return;
        }

        var cancellationToken = _pageLifetime.Token;
        Task<AniMeido.Contracts.Models.Anime?>? request = null;
        bool IsCurrentRequest()
            => !cancellationToken.IsCancellationRequested
                && GetArchiveCoverAnimeId(image.DataContext) == animeId
                && _coverRequests.IsCurrent(animeId.Value, request);

        try
        {
            request = _coverRequests.GetOrStart(animeId.Value, cancellationToken);

            // 详情已取到时同步配置：同一封面不会被清空重载。
            // 还要等待时先清掉，复用的容器不留上一部作品的封面。
            if (!request.IsCompletedSuccessfully)
                ManagedImageLoader.Cancel(image);
            var anime = await request;
            if (IsCurrentRequest())
            {
                ManagedImageLoader.ConfigureCover(
                    image,
                    animeId.Value,
                    anime?.CoverURL,
                    decodeWidth);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or MetadataServiceException
            or IOException or InvalidOperationException
            or TaskCanceledException or JsonException)
        {
            // 失败请求下次可重试，但不能清掉复用容器或替代请求的新封面。
            if (IsCurrentRequest())
                ManagedImageLoader.Cancel(image);
        }
    }

    private void OnScreenshotImageLoaded(object sender, RoutedEventArgs e)
        => ConfigureScreenshotImage(sender as Image);

    private void OnScreenshotImageDataContextChanged(
        FrameworkElement sender,
        DataContextChangedEventArgs args)
    {
        _ = args;
        ConfigureScreenshotImage(sender as Image);
    }

    private void ApplyScreenshotFilter()
    {
        if (_pageLifetime is null || _screenshotGroups is null || ScreenshotList is null)
            return;

        var selectedIds = ScreenshotList.SelectedItems
            .OfType<AnimeScreenshot>()
            .Select(item => item.ScreenshotId)
            .ToHashSet(StringComparer.Ordinal);
        var year = double.IsNaN(ScreenshotYearFilter.Value)
            ? null
            : (int?)ScreenshotYearFilter.Value;
        var filtered = ViewModels.ArchiveBrowse.FilterScreenshots(
            _panels.Screenshots, _panels.ScreenshotTags, ScreenshotFilter.Text.Trim(), year);
        if (ScreenshotUnassignedOnly.IsChecked == true)
            filtered = filtered.Where(item => item.AnimeId is null).ToArray();
        var requestedScreenshotId = _requestedScreenshotId;
        var page = requestedScreenshotId is null
            ? ViewModels.DesktopScreenshotPaging.Select(filtered, _desktopScreenshotPage)
            : ViewModels.DesktopScreenshotPaging.SelectContaining(
                filtered, item => item.ScreenshotId == requestedScreenshotId, _desktopScreenshotPage);
        _requestedScreenshotId = null;
        _desktopScreenshotPage = page.Index;
        _desktopScreenshotPageCount = page.PageCount;
        ScreenshotPageText.Text = $"{page.Index + 1} / {page.PageCount}";
        ScreenshotPreviousButton.IsEnabled = page.HasPrevious;
        ScreenshotNextButton.IsEnabled = page.HasNext;
        var restoredSelection = _gallery.ApplyFilter(
            page.Items, _panels.ScreenshotTags, string.Empty, null, selectedIds);

        _suppressScreenshotSelectionChanged = true;
        try
        {
            _screenshotGroups.Source = _gallery.VisibleScreenshots
                .GroupBy(item => item.CapturedAt.ToLocalTime().Date)
                .Select(group => new ScreenshotDayGroup(group.Key, group))
                .ToArray();
            if (_gallery.IsBatchMode)
            {
                foreach (var item in restoredSelection)
                {
                    if (!ScreenshotList.SelectedItems.Contains(item))
                        ScreenshotList.SelectedItems.Add(item);
                }
            }
            else
            {
                ScreenshotList.SelectedItem = page.Items.FirstOrDefault(item => item.ScreenshotId == requestedScreenshotId)
                    ?? restoredSelection.FirstOrDefault();
            }
        }
        finally
        {
            _suppressScreenshotSelectionChanged = false;
        }

        _gallery.CompleteFilterSelection(
            (ScreenshotList.SelectedItem as AnimeScreenshot)?.ScreenshotId);
        ScreenshotCountText.Text = $"截图 {page.TotalCount} 张";
        ScreenshotEmptyState.Visibility = _gallery.VisibleScreenshots.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateScreenshotDetails();
    }

    private void OnScreenshotSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_screenshotGroups is null || _suppressScreenshotSelectionChanged)
            return;

        _gallery.UpdateSelection(
            e.AddedItems.OfType<AnimeScreenshot>().LastOrDefault()?.ScreenshotId,
            (ScreenshotList.SelectedItem as AnimeScreenshot)?.ScreenshotId);
        UpdateScreenshotDetails();
    }

    private void OnScreenshotBatchClick(object sender, RoutedEventArgs e)
    {
        _gallery.ToggleBatchMode();
        _suppressScreenshotSelectionChanged = true;
        try
        {
            ScreenshotList.SelectionMode = _gallery.IsBatchMode
                ? ListViewSelectionMode.Multiple : ListViewSelectionMode.Single;
            ScreenshotBatchButton.Content = _gallery.IsBatchMode
                ? "完成选择" : "批量选择";
            ScreenshotBatchActions.Visibility = _gallery.IsBatchMode
                ? Visibility.Visible : Visibility.Collapsed;
            ScreenshotDetailMoreButton.Visibility = _gallery.IsBatchMode
                ? Visibility.Collapsed : Visibility.Visible;
            if (!_gallery.IsBatchMode)
                ScreenshotList.SelectedItem = _gallery.DetailScreenshot;
        }
        finally
        {
            _suppressScreenshotSelectionChanged = false;
        }
        UpdateScreenshotDetails();
    }

    private AnimeScreenshot? GetDetailScreenshot()
        => _gallery.DetailScreenshot;

    private void UpdateScreenshotDetails()
    {
        var item = GetDetailScreenshot();
        ScreenshotDetailsEmptyState.Visibility = item is null
            ? Visibility.Visible : Visibility.Collapsed;
        ScreenshotDetailsPanel.Visibility = item is null
            ? Visibility.Collapsed : Visibility.Visible;
        ScreenshotSelectedCountText.Text = $"已选 {ScreenshotList.SelectedItems.Count} 张";
        ScreenshotBatchTagButton.IsEnabled = ScreenshotList.SelectedItems.Count > 0;
        ScreenshotBatchExportButton.IsEnabled = ScreenshotList.SelectedItems
            .OfType<AnimeScreenshot>().Any(selected => selected.FileExists);
        if (!ReferenceEquals(ScreenshotDetailImage.DataContext, item))
            ScreenshotDetailImage.DataContext = item;
        if (item is null)
            return;

        ScreenshotDetailAnime.Text = item.ReviewTitle;
        ScreenshotDetailTime.Text = item.CapturedAt.ToLocalTime()
            .ToString("yyyy/MM/dd HH:mm", CultureInfo.CurrentCulture);
        ScreenshotDetailEpisode.Text = item.EpisodeNumber is { } episode
            ? $"第 {episode} 集" : "未设置";
        ScreenshotDetailPosition.Text = item.PlaybackPositionSeconds is { } seconds
            ? TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss", CultureInfo.CurrentCulture)
            : "未记录";
        ScreenshotDetailSize.Text = $"{item.Width} × {item.Height}";
        var note = GetScreenshotNote(item);
        ScreenshotDetailContext.Text = string.IsNullOrWhiteSpace(note)
            ? "尚未添加备注" : note;
        ScreenshotDetailFileState.Text = item.FileExists
            ? string.Empty : "原图文件已缺失";
        ScreenshotDetailFileState.Visibility = item.FileExists
            ? Visibility.Collapsed : Visibility.Visible;
        ScreenshotOpenButton.IsEnabled = item.FileExists;
        ScreenshotExportButton.IsEnabled = item.FileExists;
        var tags = _panels.ScreenshotTags.GetValueOrDefault(item.ScreenshotId) ?? [];
        if (!ReferenceEquals(ScreenshotDetailTags.ItemsSource, tags))
            ScreenshotDetailTags.ItemsSource = tags;
    }

    private static string GetScreenshotNote(AnimeScreenshot item)
    {
        if (item.EpisodeNumber is not { } episode)
            return item.ContextNote;

        // Older captures repeated playback metadata in ContextNote. Keep the
        // note field for user text; playback metadata has its own fields.
        var generated = $"第 {episode} 集";
        if (item.PlaybackPositionSeconds is { } seconds)
        {
            var position = TimeSpan.FromSeconds(seconds);
            generated += " · " + (position.TotalHours >= 1
                ? position.ToString(@"h\:mm\:ss")
                : position.ToString(@"m\:ss"));
        }
        return string.Equals(item.ContextNote, generated, StringComparison.Ordinal)
            ? string.Empty : item.ContextNote;
    }

    private void OnScreenshotFilterChanged(
        object sender,
        TextChangedEventArgs e)
    {
        _desktopScreenshotPage = 0;
        ApplyScreenshotFilter();
    }

    private void OnScreenshotYearFilterChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args)
    {
        _desktopScreenshotPage = 0;
        ApplyScreenshotFilter();
    }

    private void RenderStatistics(ArchiveStatistics statistics)
    {
        StatisticsStartText.Text = statistics.RecordingStartedAt is { } started
            ? $"统计起点 {started.ToLocalTime():yyyy/MM/dd}"
            : "尚无记录";
        StatisticsArchiveCountText.Text = $"{statistics.ArchiveCount} 部";
        StatisticsRatedCountText.Text = $"{statistics.RatedCount} 部";
        StatisticsEntryCountText.Text = $"{statistics.EntryCount} 条";
        StatisticsScreenshotCountText.Text = $"{statistics.ScreenshotCount} 张";

        var tags = statistics.TagCounts
            .OrderByDescending(item => item.Value)
            .ThenBy(item => item.Key, StringComparer.CurrentCulture)
            .Take(6)
            .ToArray();
        var maximum = tags.Length == 0 ? 0 : tags[0].Value;
        StatisticsTagList.ItemsSource = tags.Select(item => new ArchiveTagBarData(
            item.Key,
            item.Value,
            maximum > 0 ? item.Value * 100d / maximum : 0)).ToArray();
        StatisticsTagsEmpty.Visibility = tags.Length == 0
            ? Visibility.Visible : Visibility.Collapsed;

        StatisticsCompletedText.Text = $"{statistics.CompletedEpisodeCount} 集";
        var minutes = Math.Max(0, statistics.EstimatedWatchMinutes);
        StatisticsDurationText.Text = $"{minutes / 60} 小时 {minutes % 60} 分钟";

        var coverage = statistics.ArchiveCount > 0
            ? Math.Clamp(statistics.RatedCount * 100d / statistics.ArchiveCount, 0, 100)
            : 0;
        StatisticsCoverageBar.Value = coverage;
        StatisticsCoveragePercentText.Text = $"{coverage:0}%";
        StatisticsCoverageText.Text = $"{statistics.RatedCount} / {statistics.ArchiveCount} 部";
        StatisticsUnratedText.Text = $"{Math.Max(0, statistics.ArchiveCount - statistics.RatedCount)} 部未评分";
        StatisticsStatusChangesText.Text = $"{statistics.TrackingChangeCount} 次";
    }

    private void RenderReview(
        int year,
        ArchiveStatistics statistics,
        IReadOnlyList<ArchiveReviewCover> covers,
        IReadOnlyList<AnnualReviewMoment> moments,
        IReadOnlyList<AnimeScreenshot> screenshots)
    {
        ReviewHeroYear.Text = year.ToString(CultureInfo.CurrentCulture);
        ReviewYearState.Text = year switch
        {
            var selected when selected < DateTime.Now.Year => "这一年已结束",
            var selected when selected > DateTime.Now.Year => "这一年尚未开始",
            _ => $"截至 {DateTime.Now:M月d日} · 本年度尚未结束",
        };
        ReviewRecordingStart.Text = statistics.RecordingStartedAt is { } started
            ? $"记录起点：{started.ToLocalTime():yyyy/MM/dd}"
            : "记录起点：暂无";
        ReviewArchiveCount.Text = $"{statistics.ArchiveCount} 部";
        ReviewEntryCount.Text = $"{statistics.EntryCount} 条";
        ReviewScreenshotCount.Text = $"{statistics.ScreenshotCount} 张";
        var minutes = Math.Max(0, statistics.EstimatedWatchMinutes);
        ReviewWatchDuration.Text = $"{minutes / 60} 小时 {minutes % 60} 分钟";

        ReviewCovers.ItemsSource = covers;
        ReviewCoversEmpty.Visibility = covers.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        ReviewMoments.ItemsSource = moments;
        ReviewMomentsEmpty.Visibility = moments.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;

        var images = screenshots
            .Where(item => item.FileExists
                && item.CapturedAt.ToLocalTime().Year == year)
            .Take(2)
            .ToArray();
        ReviewScreenshots.ItemsSource = images;
        ReviewScreenshotsEmpty.Visibility = images.Length == 0
            ? Visibility.Visible : Visibility.Collapsed;
        ApplyPlaybackAvailability();
    }

    private void OnArchiveDetailsSelectionChanged(ArchiveListItem? selectedArchive, int selectionVersion)
    {
        var previous = _presentedArchive;
        var changed = previous?.Archive.AnimeId != selectedArchive?.Archive.AnimeId;
        _presentedArchive = selectedArchive;
        if (changed)
        {
            ++_archiveReadingGeneration;
            EntryList.SelectedItem = null;
            ApplyTimelineFilter(preserveOffset: false);
        }

        if (selectedArchive is null)
        {
            ArchiveSelectionEmptyState.Visibility = Visibility.Visible;
            ArchiveDetailPanel.Visibility = Visibility.Collapsed;
            EntryList.ItemsSource = null;
            ArchiveScreenshotStrip.ItemsSource = null;
            ArchiveScreenshotSection.Visibility = Visibility.Collapsed;
            ManagedImageLoader.Cancel(ArchiveHeroCover);
            UpdateEntryActions();
            return;
        }

        ArchiveSelectionEmptyState.Visibility = Visibility.Collapsed;
        ArchiveDetailPanel.Visibility = Visibility.Visible;
        ArchiveTitle.Text = selectedArchive.Archive.TitleSnapshot;
        if (changed || previous?.Unavailable != selectedArchive.Unavailable)
            ArchiveAnimeMetadata.Text = selectedArchive.Archive.AnimeId < 0 || selectedArchive.Unavailable ? "资料不可用" : "";
        ArchiveRatingText.Text = selectedArchive.RatingText;
        ArchiveStatusText.Text = selectedArchive.StatusText;
        ArchiveTagsText.Text = selectedArchive.Tags.Count == 0
            ? "尚无个人标签"
            : string.Join(" · ", selectedArchive.Tags);
        if (changed || previous?.Archive.SummaryNote != selectedArchive.Archive.SummaryNote)
            ArchiveMarkdownRenderer.Render(ArchiveNotePreview, selectedArchive.Archive.SummaryNote);
        if (changed)
        {
            ArchiveScreenshotStrip.ItemsSource = null;
            ArchiveScreenshotSection.Visibility = Visibility.Collapsed;
            ArchiveScreenshotCount.Text = string.Empty;
            ManagedImageLoader.Cancel(ArchiveHeroCover);
            QueueArchiveTitleReset();
        }
        if (_pageLifetime is { } lifetime)
        {
            _ = ConfigureHeroCoverAsync(selectedArchive.Archive.AnimeId, selectionVersion, lifetime.Token);
        }
    }

    private void OnArchiveDetailsPublished()
    {
        var screenshots = _details.DetailScreenshots;
        ArchiveScreenshotCount.Text = $"{screenshots.Count} 张";
        ArchiveScreenshotSection.Visibility = screenshots.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        var visible = screenshots.Take(2).ToArray();
        if (ArchiveScreenshotStrip.ItemsSource is not IEnumerable<AnimeScreenshot> previous || !previous.SequenceEqual(visible))
            ArchiveScreenshotStrip.ItemsSource = visible;
    }

    private async Task ConfigureHeroCoverAsync(
        int animeId,
        int selectionVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = _coverRequests.GetOrStart(animeId, cancellationToken);
            var anime = await request;
            if (_details.IsCurrentSelection(selectionVersion)
                && !cancellationToken.IsCancellationRequested)
            {
                // 作品 ID 是内部标识（迁移后已不是 Bangumi 条目号，负数表示资料里没有），不显示给用户。
                ArchiveAnimeMetadata.Text = anime is null || animeId < 0
                    ? "资料不可用"
                    : string.Join("   ", new[]
                    {
                        anime.AirDate?.Year.ToString(),
                        ViewModels.AnimeReleaseClassifier.GetMediaFormatText(anime.MediaFormat),
                    }.Where(value => !string.IsNullOrWhiteSpace(value)));
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or MetadataServiceException
            or IOException or InvalidOperationException
            or TaskCanceledException or JsonException)
        {
            // 主视觉留空即可；失败的请求下次取用时会重新发起。
        }
    }

    private void OnTimelineFilterChanged(
        object sender,
        SelectionChangedEventArgs e)
        => ApplyTimelineFilter();

    private void ApplyTimelineFilter() => ApplyTimelineFilter(preserveOffset: true);

    private void ApplyTimelineFilter(bool preserveOffset)
    {
        if (EntryList is null || ArchiveTimelineFilter is null)
            return;

        var kind = ArchiveTimelineFilter.SelectedIndex switch
        {
            1 => "感想",
            2 => "观看记录",
            3 => "状态变更",
            _ => null,
        };
        var items = kind is null
            ? _details.TimelineItems
            : _details.TimelineItems.Where(item => item.Kind == kind).ToArray();
        if (EntryList.ItemsSource is not IEnumerable<ArchiveTimelineItem> previous || !previous.SequenceEqual(items))
        {
            var selected = EntryList.SelectedItem as ArchiveTimelineItem;
            var generation = _archiveReadingGeneration;
            ReplaceArchiveListSource(EntryList, items, preserveOffset, () => generation == _archiveReadingGeneration);
            if (selected is not null)
                EntryList.SelectedItem = items.FirstOrDefault(item => item == selected
                    || (item.Entry is not null && item.Entry.EntryId == selected.Entry?.EntryId));
        }
        UpdateEntryActions();
    }

    private void QueueArchiveTitleReset()
    {
        if (_presentedArchive is null || _pageLifetime is not { } lifetime) return;
        var visit = _navigationVersion;
        var generation = _archiveReadingGeneration;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!IsNavigationCurrent(visit, lifetime) || generation != _archiveReadingGeneration
                || _panels.ActivePanel != ArchivePanelKind.Archives) return;
            // 宽窄布局只移动详情面板所在的格子；两处都用自己的正文滚动区。
            ArchiveDetailPanel.ChangeView(null, 0, null, disableAnimation: true);
            FindArchiveScrollViewer(EntryList)?.ChangeView(null, 0, null, disableAnimation: true);
        });
    }

    private static void ReplaceArchiveListSource(ListView list, object source, bool preserveOffset = true, Func<bool>? canRestore = null)
    {
        var viewer = preserveOffset ? FindArchiveScrollViewer(list) : null;
        var offset = viewer?.VerticalOffset ?? 0;
        list.ItemsSource = source;
        if (offset <= 0 || viewer is null) return;
        list.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (list.IsLoaded && ReferenceEquals(list.ItemsSource, source) && canRestore?.Invoke() != false)
                viewer.ChangeView(null, Math.Min(offset, viewer.ScrollableHeight), null, disableAnimation: true);
        });
    }

    private static ScrollViewer? FindArchiveScrollViewer(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindArchiveScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }

    private void OnEntrySelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
        => UpdateEntryActions();

    private void UpdateEntryActions()
    {
        var hasSelection = EntryList.SelectedItem is ArchiveTimelineItem
            { Entry: not null };
        EditEntryButton.IsEnabled = hasSelection;
        DeleteEntryButton.IsEnabled = hasSelection;
    }

    private void ShowPanel(string tag)
    {
        var nextPanel = tag switch
        {
            "statistics" => ArchivePanelKind.Statistics,
            "review" => ArchivePanelKind.Review,
            "screenshots" => ArchivePanelKind.Screenshots,
            _ => ArchivePanelKind.Archives,
        };
        _panels.SelectPanel(nextPanel);
        ArchivesPanel.Visibility =
            tag == "archives" ? Visibility.Visible : Visibility.Collapsed;
        StatisticsPanel.Visibility =
            tag == "statistics" ? Visibility.Visible : Visibility.Collapsed;
        ReviewPanel.Visibility =
            tag == "review" ? Visibility.Visible : Visibility.Collapsed;
        ScreenshotsPanel.Visibility =
            tag == "screenshots" ? Visibility.Visible : Visibility.Collapsed;
        ArchivesSectionButton.IsChecked = tag == "archives";
        StatisticsSectionButton.IsChecked = tag == "statistics";
        ReviewSectionButton.IsChecked = tag == "review";
        ScreenshotsSectionButton.IsChecked = tag == "screenshots";
    }

    private void OnShowScreenshotMoreActionsClick(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase
                .ShowAttachedFlyout(element);
        }
    }

    private void OnReviewPreviousYearClick(object sender, RoutedEventArgs e)
    {
        var year = double.IsNaN(ReviewYear.Value)
            ? DateTime.Now.Year : (int)ReviewYear.Value;
        ReviewYear.Value = Math.Max(2000, year - 1);
    }

    private void OnReviewNextYearClick(object sender, RoutedEventArgs e)
    {
        var year = double.IsNaN(ReviewYear.Value)
            ? DateTime.Now.Year : (int)ReviewYear.Value;
        ReviewYear.Value = Math.Min(2100, year + 1);
    }

    private void ApplyPlaybackAvailability()
    {
        var available = _playbackLauncher.IsAvailable;
        var playbackVisibility = available
            ? Visibility.Visible : Visibility.Collapsed;
        ScreenshotDetailEpisodeLabel.Visibility = playbackVisibility;
        ScreenshotDetailEpisode.Visibility = playbackVisibility;
        ScreenshotDetailPositionLabel.Visibility = playbackVisibility;
        ScreenshotDetailPosition.Visibility = playbackVisibility;
        StatisticsWatchCard.Visibility = available
            ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan(StatisticsTagsCard, available ? 1 : 2);
        ReviewScreenshotsCard.Visibility = available
            ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan(ReviewMomentsCard, available ? 1 : 2);
        ReviewScreenshotStat.Visibility = available
            ? Visibility.Visible : Visibility.Collapsed;
        ReviewWatchStat.Visibility = available
            ? Visibility.Visible : Visibility.Collapsed;
        var statWidth = available
            ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ReviewStatsGrid.ColumnDefinitions[2].Width = statWidth;
        ReviewStatsGrid.ColumnDefinitions[3].Width = statWidth;
        ArchiveTimelineTitle.Text = available
            ? "观看时间线" : "档案时间线";
        AddManualWatchButton.Visibility = available
            ? Visibility.Visible : Visibility.Collapsed;
        WatchHistoryFilterItem.Visibility = available
            ? Visibility.Visible : Visibility.Collapsed;
        if (!available)
        {
            if (ArchiveTimelineFilter.SelectedIndex == 2)
                ArchiveTimelineFilter.SelectedIndex = 0;
            _details.ApplyPlaybackAvailability(false);
        }
        ApplyDesktopArchiveSupplementalLayout();
    }

    private void ShowStatus(string message, InfoBarSeverity severity, bool transient = false)
    {
        if ((transient || severity == InfoBarSeverity.Success)
            && DesktopFeedback.TryShow(message,
                error: severity is InfoBarSeverity.Error or InfoBarSeverity.Warning))
            return;
        StatusInfoBar.Message = message;
        StatusInfoBar.Severity = severity;
        StatusInfoBar.IsOpen = true;
    }

    private static IEnumerable<string> SplitTags(string value)
        => value.Split(
            [',', '，'],
            StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries);
}

public sealed record ArchiveTagBarData(string Name, int Count, double Percent);

public sealed class ScreenshotDayGroup : List<AnimeScreenshot>
{
    public ScreenshotDayGroup(
        DateTime date,
        IEnumerable<AnimeScreenshot> screenshots)
        : base(screenshots)
    {
        Header = $"{date:yyyy年M月d日} · {Count} 张";
    }

    public string Header { get; }
}
