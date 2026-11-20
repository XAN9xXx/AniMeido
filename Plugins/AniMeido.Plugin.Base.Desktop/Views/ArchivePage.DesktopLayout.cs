using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class ArchivePage
{
    private bool? _desktopArchiveStacked;
    private int? _desktopScreenshotDetailWidth;
    private bool? _desktopScreenshotToolsStacked;
    private double _desktopArchiveContentWidth;

    private void OnDesktopArchiveSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (args.NewSize.Width <= 0) return;
        _desktopArchiveContentWidth = Math.Max(0,
            args.NewSize.Width - DesktopArchiveRoot.Padding.Left - DesktopArchiveRoot.Padding.Right);
        ApplyDesktopArchiveSupplementalLayout();
        ApplyDesktopScreenshotLayout();
        var stacked = args.NewSize.Width < 1000;
        if (_desktopArchiveStacked == stacked) return;
        _desktopArchiveStacked = stacked;
        ArchivesPanel.RowSpacing = stacked ? 16 : 0;

        ArchivesPanel.ColumnDefinitions[0].Width = stacked
            ? new GridLength(1, GridUnitType.Star) : new GridLength(240);
        ArchivesPanel.ColumnDefinitions[1].Width = stacked
            ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ArchivesPanel.RowDefinitions[0].Height = stacked
            ? new GridLength(220) : new GridLength(1, GridUnitType.Star);
        ArchivesPanel.RowDefinitions[1].Height = stacked
            ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(DesktopArchiveDetailPane, stacked ? 1 : 0);
        Grid.SetColumn(DesktopArchiveDetailPane, stacked ? 0 : 1);
    }

    private void ApplyDesktopScreenshotLayout()
    {
        var detailWidth = _desktopArchiveContentWidth >= 1000 ? 360
            : _desktopArchiveContentWidth >= 680 ? 260 : 0;
        if (_desktopScreenshotDetailWidth == detailWidth) return;
        _desktopScreenshotDetailWidth = detailWidth;
        var stacked = detailWidth == 0;
        ScreenshotsPanel.RowSpacing = stacked ? 16 : 0;
        ScreenshotsPanel.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        ScreenshotsPanel.ColumnDefinitions[1].Width = new GridLength(detailWidth);
        ScreenshotsPanel.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
        ScreenshotsPanel.RowDefinitions[1].Height = new GridLength(stacked ? 188 : 0);
        Grid.SetRow(DesktopScreenshotDetailPane, stacked ? 1 : 0);
        Grid.SetColumn(DesktopScreenshotDetailPane, stacked ? 0 : 1);
        DesktopScreenshotDetailPane.Padding = new Thickness(stacked ? 12 : 16);
        ScreenshotDetailsPanel.RowSpacing = stacked ? 8 : 12;
        DesktopScreenshotDetailContent.ColumnDefinitions[0].Width = stacked
            ? new GridLength(156) : new GridLength(1, GridUnitType.Star);
        DesktopScreenshotDetailContent.ColumnDefinitions[1].Width = stacked
            ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        DesktopScreenshotDetailContent.RowSpacing = stacked ? 0 : 14;
        Grid.SetColumnSpan(DesktopScreenshotPreview, stacked ? 1 : 2);
        Grid.SetRow(DesktopScreenshotMetadata, stacked ? 0 : 1);
        Grid.SetColumn(DesktopScreenshotMetadata, stacked ? 1 : 0);
        Grid.SetColumnSpan(DesktopScreenshotMetadata, stacked ? 1 : 2);
        DesktopScreenshotPreview.Height = stacked ? 88 : detailWidth == 260 ? 156 : 220;
        DesktopScreenshotDetailActions.ColumnDefinitions[2].Width = stacked
            ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        DesktopScreenshotDetailActions.ColumnDefinitions[3].Width = stacked
            ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        DesktopScreenshotDetailActions.RowSpacing = stacked ? 0 : 8;
        Grid.SetRow(ScreenshotExportButton, stacked ? 0 : 1);
        Grid.SetColumn(ScreenshotExportButton, stacked ? 2 : 0);
        Grid.SetRow(ScreenshotDetailMoreButton, stacked ? 0 : 1);
        Grid.SetColumn(ScreenshotDetailMoreButton, stacked ? 3 : 1);
    }

    private void OnDesktopScreenshotToolbarSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (args.NewSize.Width <= 0) return;
        var stacked = args.NewSize.Width < 680;
        if (_desktopScreenshotToolsStacked == stacked) return;
        _desktopScreenshotToolsStacked = stacked;
        DesktopScreenshotToolbar.RowSpacing = stacked ? 8 : 0;
        Grid.SetColumnSpan(ScreenshotFilter, stacked ? 4 : 1);
        Grid.SetRow(ScreenshotYearFilter, stacked ? 1 : 0);
        Grid.SetColumn(ScreenshotYearFilter, stacked ? 0 : 1);
        ScreenshotYearFilter.Width = stacked ? 130 : double.NaN;
        ScreenshotYearFilter.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        Grid.SetRow(ScreenshotBatchButton, stacked ? 1 : 0);
        Grid.SetRow(DesktopScreenshotMoreButton, stacked ? 1 : 0);
        Grid.SetRow(ScreenshotBatchActions, stacked ? 1 : 0);
        Grid.SetColumn(ScreenshotBatchActions, stacked ? 0 : 2);
        Grid.SetColumnSpan(ScreenshotBatchActions, stacked ? 3 : 1);
    }

    private void ApplyDesktopArchiveSupplementalLayout()
    {
        var width = _desktopArchiveContentWidth;
        if (width <= 0) return;
        var playbackAvailable = _playbackLauncher.IsAvailable;
        var split = width >= 760;
        var fourMetrics = width >= 720;
        DesktopStatisticsMetrics.ColumnDefinitions[2].Width = fourMetrics ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        DesktopStatisticsMetrics.ColumnDefinitions[3].Width = fourMetrics ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        DesktopStatisticsMetrics.RowDefinitions[1].Height = fourMetrics ? new GridLength(0) : GridLength.Auto;
        DesktopStatisticsMetrics.RowSpacing = fourMetrics ? 0 : 24;
        Grid.SetRow(DesktopStatisticsEntryMetric, fourMetrics ? 0 : 1);
        Grid.SetColumn(DesktopStatisticsEntryMetric, fourMetrics ? 2 : 0);
        Grid.SetRow(DesktopStatisticsScreenshotMetric, fourMetrics ? 0 : 1);
        Grid.SetColumn(DesktopStatisticsScreenshotMetric, fourMetrics ? 3 : 1);

        DesktopStatisticsDetails.ColumnDefinitions[1].Width = playbackAvailable && split
            ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        DesktopStatisticsDetails.RowSpacing = playbackAvailable && !split ? 20 : 0;
        Grid.SetColumnSpan(StatisticsTagsCard, playbackAvailable && split ? 1 : 2);
        Grid.SetRow(StatisticsWatchCard, split ? 0 : 1);
        Grid.SetColumn(StatisticsWatchCard, split ? 1 : 0);
        Grid.SetColumnSpan(StatisticsWatchCard, split ? 1 : 2);

        DesktopStatisticsCoverage.ColumnDefinitions[1].Width = new GridLength(split ? 220 : 0);
        DesktopStatisticsCoverage.RowSpacing = split ? 0 : 20;
        Grid.SetRow(DesktopStatisticsStatus, split ? 0 : 1);
        Grid.SetColumn(DesktopStatisticsStatus, split ? 1 : 0);
        Grid.SetColumnSpan(DesktopStatisticsStatus, split ? 1 : 2);

        var reviewWidth = Math.Min(850, width);
        DesktopReviewContent.Width = reviewWidth;
        var reviewToolbarStacked = reviewWidth < 520;
        DesktopReviewToolbar.RowSpacing = reviewToolbarStacked ? 12 : 0;
        Grid.SetRow(DesktopReviewExportButton, reviewToolbarStacked ? 1 : 0);
        Grid.SetColumn(DesktopReviewExportButton, reviewToolbarStacked ? 0 : 1);
        DesktopReviewExportButton.HorizontalAlignment = reviewToolbarStacked ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        DesktopReviewHero.ColumnDefinitions[1].Width = playbackAvailable
            ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        DesktopReviewCoversPane.Visibility = playbackAvailable ? Visibility.Visible : Visibility.Collapsed;

        var fourReviewMetrics = playbackAvailable && reviewWidth >= 680;
        ReviewStatsGrid.ColumnDefinitions[2].Width = fourReviewMetrics ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ReviewStatsGrid.ColumnDefinitions[3].Width = fourReviewMetrics ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ReviewStatsGrid.RowDefinitions[1].Height = playbackAvailable && !fourReviewMetrics ? GridLength.Auto : new GridLength(0);
        ReviewStatsGrid.RowSpacing = playbackAvailable && !fourReviewMetrics ? 20 : 0;
        Grid.SetRow(ReviewScreenshotStat, fourReviewMetrics ? 0 : 1);
        Grid.SetColumn(ReviewScreenshotStat, fourReviewMetrics ? 2 : 0);
        Grid.SetRow(ReviewWatchStat, fourReviewMetrics ? 0 : 1);
        Grid.SetColumn(ReviewWatchStat, fourReviewMetrics ? 3 : 1);

        var splitReview = playbackAvailable && reviewWidth >= 720;
        DesktopReviewDetails.ColumnDefinitions[1].Width = splitReview ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        DesktopReviewDetails.RowSpacing = playbackAvailable && !splitReview ? 24 : 0;
        Grid.SetColumnSpan(ReviewMomentsCard, splitReview ? 1 : 2);
        Grid.SetRow(ReviewScreenshotsCard, splitReview ? 0 : 1);
        Grid.SetColumn(ReviewScreenshotsCard, splitReview ? 1 : 0);
        Grid.SetColumnSpan(ReviewScreenshotsCard, splitReview ? 1 : 2);
    }
}
