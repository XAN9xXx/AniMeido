using AniMeido.Contracts;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Globalization;
using System.Net;
using System.Text;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class ArchivePage : Page, INavigationAware
{
    private readonly ArchiveService _archive;
    private readonly ScreenshotArchiveService _screenshots;
    private readonly ScreenshotShortcutAction _shortcut;
    private IReadOnlyList<ArchiveListItem> _allArchives = [];
    private IReadOnlyList<AnimeScreenshot> _allScreenshots = [];
    private readonly Dictionary<string, IReadOnlyList<string>>
        _screenshotTags = new(StringComparer.Ordinal);
    private ArchiveListItem? _selectedArchive;
    private string? _requestedScreenshotId;
    private int? _requestedAnimeId;

    public ArchivePage(
        ArchiveService archive,
        ScreenshotArchiveService screenshots,
        ScreenshotShortcutAction shortcut)
    {
        _archive = archive;
        _screenshots = screenshots;
        _shortcut = shortcut;
        InitializeComponent();
        StatusFilter.SelectedIndex = 0;
        ReviewYear.Value = DateTime.Now.Year;
        SectionNavigation.SelectedItem =
            SectionNavigation.MenuItems[0];
    }

    public async Task OnNavigatedToAsync(object? parameter)
    {
        _requestedScreenshotId = parameter as string;
        _requestedAnimeId = parameter as int?;
        await LoadAsync();
        if (_requestedAnimeId is { } animeId)
        {
            ArchiveList.SelectedItem = ArchiveList.Items
                .OfType<ArchiveListItem>()
                .FirstOrDefault(item => item.Archive.AnimeId == animeId);
            if (ArchiveList.SelectedItem is not null)
            {
                ArchiveList.ScrollIntoView(ArchiveList.SelectedItem);
            }
        }
        if (_requestedScreenshotId is not null)
        {
            SectionNavigation.SelectedItem =
                SectionNavigation.MenuItems[3];
            ShowPanel("screenshots");
            ScreenshotList.SelectedItem = ScreenshotList.Items
                .OfType<AnimeScreenshot>()
                .FirstOrDefault(item =>
                    item.ScreenshotId == _requestedScreenshotId);
            if (ScreenshotList.SelectedItem is not null)
            {
                ScreenshotList.ScrollIntoView(
                    ScreenshotList.SelectedItem);
            }
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            _allArchives = await _archive.GetArchiveListAsync();
            ApplyArchiveFilter();
            await ReloadScreenshotsAsync();
            await LoadStatisticsAsync();
            StatusInfoBar.IsOpen = false;
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
                or IOException
                or Microsoft.Data.Sqlite.SqliteException)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task LoadStatisticsAsync()
    {
        var statistics = await _archive.GetStatisticsAsync();
        StatisticsText.Text = FormatStatistics(statistics);
        await LoadReviewAsync();
    }

    private async Task ReloadScreenshotsAsync()
    {
        _allScreenshots = await _archive.GetScreenshotsAsync();
        _screenshotTags.Clear();
        foreach (var screenshot in _allScreenshots)
        {
            _screenshotTags[screenshot.ScreenshotId] =
                await _archive.GetScreenshotTagsAsync(
                    screenshot.ScreenshotId);
        }

        ApplyScreenshotFilter();
    }

    private void ApplyScreenshotFilter()
    {
        var filter = ScreenshotFilter.Text.Trim();
        var year = double.IsNaN(ScreenshotYearFilter.Value)
            ? null
            : (int?)ScreenshotYearFilter.Value;
        ScreenshotList.ItemsSource = _allScreenshots.Where(item =>
            (year is null
                || item.CapturedAt.ToLocalTime().Year == year)
            && (filter.Length == 0
                || (item.AnimeTitle?.Contains(
                    filter,
                    StringComparison.CurrentCultureIgnoreCase) ?? false)
                || item.ContextNote.Contains(
                    filter,
                    StringComparison.CurrentCultureIgnoreCase)
                || item.ProcessName.Contains(
                    filter,
                    StringComparison.CurrentCultureIgnoreCase)
                || (_screenshotTags.TryGetValue(
                        item.ScreenshotId,
                        out var tags)
                    && tags.Any(tag => tag.Contains(
                        filter,
                        StringComparison.CurrentCultureIgnoreCase)))))
            .ToArray();
    }

    private void OnScreenshotFilterChanged(
        object sender,
        TextChangedEventArgs e)
        => ApplyScreenshotFilter();

    private void OnScreenshotYearFilterChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args)
        => ApplyScreenshotFilter();

    private async Task LoadReviewAsync()
    {
        var year = double.IsNaN(ReviewYear.Value)
            ? DateTime.Now.Year
            : (int)ReviewYear.Value;
        var statistics = await _archive.GetStatisticsAsync(year);
        ReviewText.Text = FormatStatistics(statistics);
    }

    private static string FormatStatistics(ArchiveStatistics statistics)
    {
        var started = statistics.RecordingStartedAt?.ToLocalTime()
            .ToString("yyyy-MM-dd", CultureInfo.CurrentCulture)
            ?? "尚无记录";
        var tags = statistics.TagCounts.Count == 0
            ? "暂无"
            : string.Join(
                "、",
                statistics.TagCounts.Take(8)
                    .Select(item => $"{item.Key}（{item.Value}）"));
        return $"""
            统计起点：{started}
            档案：{statistics.ArchiveCount}
            已评分：{statistics.RatedCount}
            感想：{statistics.EntryCount}
            截图：{statistics.ScreenshotCount}
            状态变化：{statistics.TrackingChangeCount}
            完成集数：{statistics.CompletedEpisodeCount}
            估算观看时长：{statistics.EstimatedWatchMinutes} 分钟
            常用个人标签：{tags}
            """;
    }

    private void ApplyArchiveFilter()
    {
        var filter = ArchiveFilter.Text.Trim();
        var status = StatusFilter.SelectedIndex switch
        {
            1 => AniMeido.Contracts.Models.AnimeTrackingStatus.Watching,
            2 => AniMeido.Contracts.Models.AnimeTrackingStatus.PlanToWatch,
            3 => AniMeido.Contracts.Models.AnimeTrackingStatus.NotInterested,
            4 => AniMeido.Contracts.Models.AnimeTrackingStatus.Following,
            5 => AniMeido.Contracts.Models.AnimeTrackingStatus.Completed,
            6 => AniMeido.Contracts.Models.AnimeTrackingStatus.Dropped,
            _ => (AniMeido.Contracts.Models.AnimeTrackingStatus?)null,
        };
        double? minimumRating = double.IsNaN(MinimumRatingFilter.Value)
            ? null
            : MinimumRatingFilter.Value;
        var year = double.IsNaN(ArchiveYearFilter.Value)
            ? null
            : (int?)ArchiveYearFilter.Value;
        ArchiveList.ItemsSource = _allArchives.Where(item =>
            item.TrackingStatus
                != AniMeido.Contracts.Models.AnimeTrackingStatus.Blocked
            && (string.IsNullOrWhiteSpace(filter)
                || item.Archive.TitleSnapshot.Contains(
                    filter,
                    StringComparison.CurrentCultureIgnoreCase)
                || item.Tags.Any(tag => tag.Contains(
                    filter,
                    StringComparison.CurrentCultureIgnoreCase)))
            && (status is null || item.TrackingStatus == status)
            && (minimumRating is null
                || item.Archive.PersonalRating >= minimumRating)
            && (year is null
                || item.Archive.CreatedAt.ToLocalTime().Year == year))
            .ToList();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
        => await LoadAsync();

    private void OnArchiveFilterChanged(
        object sender,
        TextChangedEventArgs e)
        => ApplyArchiveFilter();

    private void OnArchiveOptionChanged(
        object sender,
        SelectionChangedEventArgs e)
        => ApplyArchiveFilter();

    private void OnArchiveNumberFilterChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args)
        => ApplyArchiveFilter();

    private async void OnArchiveSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        _selectedArchive = ArchiveList.SelectedItem as ArchiveListItem;
        if (_selectedArchive is null)
        {
            return;
        }

        ArchiveTitle.Text = _selectedArchive.Archive.TitleSnapshot;
        RatingBox.Value =
            _selectedArchive.Archive.PersonalRating ?? double.NaN;
        SummaryBox.Text = _selectedArchive.Archive.SummaryNote;
        TagsBox.Text = string.Join(", ", _selectedArchive.Tags);
        EntryList.ItemsSource = await _archive.GetEntriesAsync(
            _selectedArchive.Archive.AnimeId);
        WatchHistoryList.ItemsSource = await _archive.GetWatchHistoryAsync(
            _selectedArchive.Archive.AnimeId);
    }

    private async void OnSaveArchiveClick(
        object sender,
        RoutedEventArgs e)
    {
        if (_selectedArchive is null)
        {
            ShowStatus("请先选择一部番剧。", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            double? rating = double.IsNaN(RatingBox.Value)
                ? null
                : RatingBox.Value;
            await _archive.UpsertArchiveAsync(
                _selectedArchive.Archive.AnimeId,
                _selectedArchive.Archive.TitleSnapshot,
                rating,
                SummaryBox.Text);
            await _archive.SetAnimeTagsAsync(
                _selectedArchive.Archive.AnimeId,
                SplitTags(TagsBox.Text));
            await LoadAsync();
            ShowStatus("档案已保存。", InfoBarSeverity.Success);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Warning);
        }
    }

    private async void OnAddEntryClick(object sender, RoutedEventArgs e)
    {
        if (_selectedArchive is null)
        {
            ShowStatus("请先选择一部番剧。", InfoBarSeverity.Warning);
            return;
        }

        var body = new TextBox
        {
            Header = "观看感想",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 120,
        };
        var episode = new NumberBox
        {
            Header = "集数（可选）",
            Minimum = 1,
            SpinButtonPlacementMode =
                NumberBoxSpinButtonPlacementMode.Compact,
        };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(episode);
        panel.Children.Add(body);
        var dialog = CreateDialog("追加观看感想", panel, "保存");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary
            && !string.IsNullOrWhiteSpace(body.Text))
        {
            await _archive.AddEntryAsync(
                _selectedArchive.Archive.AnimeId,
                DateTimeOffset.Now,
                double.IsNaN(episode.Value) ? null : (int)episode.Value,
                body.Text);
            EntryList.ItemsSource = await _archive.GetEntriesAsync(
                _selectedArchive.Archive.AnimeId);
            await LoadStatisticsAsync();
        }
    }

    private async void OnEditEntryClick(
        object sender,
        RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is not ArchiveEntry entry)
        {
            ShowStatus("请先选择一条感想。", InfoBarSeverity.Warning);
            return;
        }

        var body = new TextBox
        {
            Header = "观看感想",
            Text = entry.Body,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 120,
        };
        var episode = new NumberBox
        {
            Header = "集数（可选）",
            Minimum = 1,
            Value = entry.EpisodeNumber ?? double.NaN,
        };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(episode);
        panel.Children.Add(body);
        var dialog = CreateDialog("编辑观看感想", panel, "保存");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await _archive.UpdateEntryAsync(
                entry.EntryId,
                entry.OccurredAt,
                double.IsNaN(episode.Value)
                    ? null
                    : (int)episode.Value,
                body.Text);
            EntryList.ItemsSource = await _archive.GetEntriesAsync(
                entry.AnimeId);
            await LoadStatisticsAsync();
        }
    }

    private async void OnDeleteEntryClick(
        object sender,
        RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is not ArchiveEntry entry)
        {
            ShowStatus("请先选择一条感想。", InfoBarSeverity.Warning);
            return;
        }

        var dialog = CreateDialog(
            "删除观看感想",
            "此操作无法撤销。",
            "删除");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await _archive.DeleteEntryAsync(entry.EntryId);
            EntryList.ItemsSource = await _archive.GetEntriesAsync(
                entry.AnimeId);
            await LoadStatisticsAsync();
        }
    }

    private async void OnAddManualEventClick(
        object sender,
        RoutedEventArgs e)
    {
        if (_selectedArchive is null)
        {
            ShowStatus("请先选择一部番剧。", InfoBarSeverity.Warning);
            return;
        }

        var date = new CalendarDatePicker
        {
            Header = "观看日期",
            Date = DateTimeOffset.Now,
        };
        var time = new TimePicker
        {
            Header = "观看时间",
            Time = DateTimeOffset.Now.TimeOfDay,
        };
        var from = new NumberBox
        {
            Header = "起始集",
            Minimum = 1,
            Value = 1,
        };
        var to = new NumberBox
        {
            Header = "结束集",
            Minimum = 1,
            Value = 1,
        };
        var minutes = new NumberBox
        {
            Header = "观看分钟数（可选）",
            Minimum = 1,
        };
        var note = new TextBox { Header = "备注" };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(date);
        panel.Children.Add(time);
        panel.Children.Add(from);
        panel.Children.Add(to);
        panel.Children.Add(minutes);
        panel.Children.Add(note);
        var dialog = CreateDialog("补录观看事件", panel, "保存");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || date.Date is null)
        {
            return;
        }

        try
        {
            var localDateTime = date.Date.Value.Date.Add(time.Time);
            var occurredAt = new DateTimeOffset(
                localDateTime,
                TimeZoneInfo.Local.GetUtcOffset(localDateTime));
            await _archive.AddManualWatchEventAsync(new ManualWatchEvent(
                Guid.NewGuid().ToString("N"),
                _selectedArchive.Archive.AnimeId,
                _selectedArchive.Archive.TitleSnapshot,
                occurredAt,
                (int)from.Value,
                (int)to.Value,
                double.IsNaN(minutes.Value)
                    ? null
                    : (int)minutes.Value,
                note.Text,
                DateTimeOffset.UtcNow));
            await LoadStatisticsAsync();
            WatchHistoryList.ItemsSource =
                await _archive.GetWatchHistoryAsync(
                    _selectedArchive.Archive.AnimeId);
            ShowStatus("观看事件已补录，不会修改当前进度。",
                InfoBarSeverity.Success);
        }
        catch (ArgumentException ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Warning);
        }
    }

    private void OnSectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string tag)
        {
            ShowPanel(tag);
        }
    }

    private void ShowPanel(string tag)
    {
        ArchivesPanel.Visibility =
            tag == "archives" ? Visibility.Visible : Visibility.Collapsed;
        StatisticsPanel.Visibility =
            tag == "statistics" ? Visibility.Visible : Visibility.Collapsed;
        ReviewPanel.Visibility =
            tag == "review" ? Visibility.Visible : Visibility.Collapsed;
        ScreenshotsPanel.Visibility =
            tag == "screenshots" ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnReviewYearChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args)
        => await LoadReviewAsync();

    private async void OnExportReviewClick(
        object sender,
        RoutedEventArgs e)
    {
        var year = (int)ReviewYear.Value;
        var statistics = await _archive.GetStatisticsAsync(year);
        var screenshots = (await _archive.GetScreenshotsAsync())
            .Where(item => item.FileExists
                && item.CapturedAt.ToLocalTime().Year == year)
            .Take(12)
            .ToArray();
        var html = await BuildReviewHtmlAsync(
            year,
            statistics,
            screenshots);
        var picker = new FileSavePicker
        {
            SuggestedFileName = $"AniMeido-{year}-年度回顾",
        };
        picker.FileTypeChoices.Add("HTML", [".html"]);
        InitializePicker(picker);
        var file = await picker.PickSaveFileAsync();
        if (file is not null)
        {
            await File.WriteAllTextAsync(file.Path, html);
            ShowStatus("年度回顾已导出。", InfoBarSeverity.Success);
        }
    }

    private async void OnOpenScreenshotClick(
        object sender,
        RoutedEventArgs e)
    {
        if (ScreenshotList.SelectedItem is AnimeScreenshot item
            && item.FileExists)
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(
                item.FilePath);
            await Windows.System.Launcher.LaunchFileAsync(file);
        }
    }

    private async void OnEditScreenshotClick(
        object sender,
        RoutedEventArgs e)
    {
        if (ScreenshotList.SelectedItem is not AnimeScreenshot item)
        {
            return;
        }

        var animeId = new NumberBox
        {
            Header = "Bangumi ID（留空表示未关联）",
            Minimum = 1,
            Value = item.AnimeId ?? double.NaN,
        };
        var title = new TextBox
        {
            Header = "番剧标题",
            Text = item.AnimeTitle ?? string.Empty,
        };
        var episode = new NumberBox
        {
            Header = "集数",
            Minimum = 1,
            Value = item.EpisodeNumber ?? double.NaN,
        };
        var context = new TextBox
        {
            Header = "场合备注",
            Text = item.ContextNote,
        };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(animeId);
        panel.Children.Add(title);
        panel.Children.Add(episode);
        panel.Children.Add(context);
        var dialog = CreateDialog("编辑截图", panel, "保存");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await _archive.UpdateScreenshotMetadataAsync(
            item.ScreenshotId,
            double.IsNaN(animeId.Value) ? null : (int)animeId.Value,
            title.Text,
            double.IsNaN(episode.Value) ? null : (int)episode.Value,
            context.Text);
        await ReloadScreenshotsAsync();
    }

    private async void OnTagScreenshotsClick(
        object sender,
        RoutedEventArgs e)
    {
        var selected = ScreenshotList.SelectedItems
            .OfType<AnimeScreenshot>()
            .ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        var input = new TextBox
        {
            Header = "个人标签（逗号分隔）",
        };
        var dialog = CreateDialog("批量添加标签", input, "添加");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await _archive.AddScreenshotTagsAsync(
                selected.Select(item => item.ScreenshotId).ToArray(),
                SplitTags(input.Text));
            await ReloadScreenshotsAsync();
            ShowStatus("截图标签已更新。", InfoBarSeverity.Success);
        }
    }

    private async void OnExportScreenshotsClick(
        object sender,
        RoutedEventArgs e)
    {
        var selected = ScreenshotList.SelectedItems
            .OfType<AnimeScreenshot>()
            .Where(item => item.FileExists)
            .ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        foreach (var item in selected)
        {
            var file = await Windows.Storage.StorageFile
                .GetFileFromPathAsync(item.FilePath);
            await file.CopyAsync(
                folder,
                file.Name,
                Windows.Storage.NameCollisionOption.GenerateUniqueName);
        }

        ShowStatus($"已导出 {selected.Length} 张原图。",
            InfoBarSeverity.Success);
    }

    private async void OnDeleteScreenshotClick(
        object sender,
        RoutedEventArgs e)
    {
        var selected = ScreenshotList.SelectedItems
            .OfType<AnimeScreenshot>()
            .ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var deleted = 0;
        var failed = 0;
        foreach (var item in selected)
        {
            try
            {
                await _screenshots.DeleteAsync(item);
                deleted++;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }

        await ReloadScreenshotsAsync();
        ShowStatus(
            failed == 0
                ? $"{deleted} 张截图已移入回收站。"
                : $"已删除 {deleted} 张，{failed} 张失败且保留记录。",
            failed == 0
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Warning);
    }

    private async void OnCleanupScreenshotsClick(
        object sender,
        RoutedEventArgs e)
    {
        var count = await _archive.RemoveMissingScreenshotRecordsAsync();
        await ReloadScreenshotsAsync();
        ShowStatus($"已清理 {count} 条缺失文件记录。",
            InfoBarSeverity.Success);
    }

    private async void OnScreenshotSettingsClick(
        object sender,
        RoutedEventArgs e)
    {
        var settings = await _archive.GetScreenshotSettingsAsync();
        var enabled = new CheckBox
        {
            Content = "启用 F12 全局截图（拦截 F12）",
            IsChecked = settings.Enabled,
        };
        var sound = new CheckBox
        {
            Content = "播放截图音效",
            IsChecked = settings.SoundEnabled,
        };
        var popup = new CheckBox
        {
            Content = "显示截图缩略图弹窗",
            IsChecked = settings.PopupEnabled,
        };
        var root = new TextBox
        {
            Header = "新截图保存目录",
            Text = settings.RootDirectory,
        };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(enabled);
        panel.Children.Add(sound);
        panel.Children.Add(popup);
        panel.Children.Add(root);
        var dialog = CreateDialog("截图设置", panel, "保存");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary
            && !string.IsNullOrWhiteSpace(root.Text))
        {
            var updated = new ScreenshotSettings(
                enabled.IsChecked == true,
                Path.GetFullPath(
                    Environment.ExpandEnvironmentVariables(root.Text.Trim())),
                sound.IsChecked == true,
                popup.IsChecked == true);
            await _archive.SaveScreenshotSettingsAsync(updated);
            _shortcut.SetEnabled(updated.Enabled);
        }
    }

    private ContentDialog CreateDialog(
        string title,
        object content,
        string primaryText)
        => new()
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusInfoBar.Message = message;
        StatusInfoBar.Severity = severity;
        StatusInfoBar.IsOpen = true;
    }

    private static IEnumerable<string> SplitTags(string value)
        => value.Split(
            [',', '，'],
            StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries);

    private static async Task<string> BuildReviewHtmlAsync(
        int year,
        ArchiveStatistics statistics,
        IReadOnlyList<AnimeScreenshot> screenshots)
    {
        var title = WebUtility.HtmlEncode($"{year} 年 AniMeido 年度回顾");
        var tags = WebUtility.HtmlEncode(string.Join(
            "、",
            statistics.TagCounts.Take(8).Select(item => item.Key)));
        var images = new StringBuilder();
        foreach (var screenshot in screenshots)
        {
            var bytes = await File.ReadAllBytesAsync(
                screenshot.FilePath);
            var caption = WebUtility.HtmlEncode(
                screenshot.ContextNote);
            images.Append(
                $"<figure><img src=\"data:image/png;base64,{Convert.ToBase64String(bytes)}\" alt=\"截图\"><figcaption>{caption}</figcaption></figure>");
        }
        return $$"""
            <!doctype html><html lang="zh-CN"><head><meta charset="utf-8">
            <title>{{title}}</title><style>
            body{font-family:"Segoe UI","Microsoft YaHei",sans-serif;
            max-width:900px;margin:48px auto;padding:0 24px;color:#24243a}
            .cards{display:grid;grid-template-columns:repeat(3,1fr);gap:12px}
            .card{background:#f1f0fa;border-radius:12px;padding:20px}
            .gallery{display:grid;grid-template-columns:repeat(3,1fr);gap:12px}
            figure{margin:0}img{width:100%;aspect-ratio:16/9;object-fit:cover;
            border-radius:8px}figcaption{font-size:.8rem;color:#68677b}
            strong{font-size:2rem;display:block}small{color:#68677b}
            </style></head><body><h1>{{title}}</h1>
            <p>仅统计 AniMeido 中真实保存的播放器事件与手工补录。</p>
            <div class="cards">
            <div class="card"><strong>{{statistics.ArchiveCount}}</strong><small>动画档案</small></div>
            <div class="card"><strong>{{statistics.CompletedEpisodeCount}}</strong><small>完成集数</small></div>
            <div class="card"><strong>{{statistics.EstimatedWatchMinutes}}</strong><small>估算观看分钟</small></div>
            <div class="card"><strong>{{statistics.EntryCount}}</strong><small>观看感想</small></div>
            <div class="card"><strong>{{statistics.ScreenshotCount}}</strong><small>截图</small></div>
            <div class="card"><strong>{{statistics.RatedCount}}</strong><small>已评分档案</small></div>
            <div class="card"><strong>{{statistics.TrackingChangeCount}}</strong><small>状态变化</small></div>
            </div><h2>常用标签</h2><p>{{tags}}</p>
            <h2>年度截图</h2><div class="gallery">{{images}}</div></body></html>
            """;
    }

    private static void InitializePicker(object picker)
    {
        if (AppServices.MainWindow is Microsoft.UI.Xaml.Window window)
        {
            InitializeWithWindow.Initialize(
                picker,
                WindowNative.GetWindowHandle(window));
        }
    }
}
