using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class ArchivePage
{
    private void ConfigureScreenshotImage(Image? image)
    {
        if (image is null) return;
        if (_pageLifetime is null || image.DataContext is not AnimeScreenshot item || !item.FileExists)
        { ManagedImageLoader.Cancel(image); return; }
        ManagedImageLoader.ConfigureLocal(image, item.FilePath, ReferenceEquals(image, ScreenshotDetailImage) ? 600 : 350);
    }

    private void OnArchiveImageUnloaded(object sender, RoutedEventArgs e)
    { if (sender is Image image) ManagedImageLoader.Cancel(image); }

    private async void OnOpenScreenshotClick(object sender, RoutedEventArgs e)
    {
        if (GetDetailScreenshot() is not { FileExists: true } item) return;
        try { await DesktopShellServices.OpenPath(item.FilePath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception)
        { ShowStatus("原图当前无法打开。", InfoBarSeverity.Warning); }
    }

    private async void OnEditScreenshotClick(object sender, RoutedEventArgs e)
    {
        if (GetDetailScreenshot() is not { } item || _pageLifetime is not { } lifetime || _dialog is not null || _editingScreenshot) return;
        _editingScreenshot = true;
        try
        {
            await ReadAsync(async () =>
            {
                using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                var targets = (await ReadScreenshotTargetsAsync(request.Token)).ToList();
                if (!ReferenceEquals(lifetime, _pageLifetime)) return;
                if (item.AnimeId is { } current && targets.All(a => a.AnimeId != current)) targets.Add(new(current, item.ReviewTitle));
                var assigned = new CheckBox { Content = "关联作品", IsChecked = item.AnimeId is not null };
                var choices = new ComboBox { Header = "关联作品", ItemsSource = targets, DisplayMemberPath = "Title",
                    HorizontalAlignment = HorizontalAlignment.Stretch, SelectedItem = targets.FirstOrDefault(a => a.AnimeId == item.AnimeId) };
                var query = new TextBox { PlaceholderText = "输入名称搜索其他作品" };
                var search = new Button { Content = "搜索作品" };
                search.Click += async (_, _) =>
                {
                    search.IsEnabled = false;
                    try
                    {
                        var page = await _pendingReview.SearchAsync(query.Text.Trim(), 0, request.Token);
                        if (request.IsCancellationRequested) return;
                        var selected = choices.SelectedItem;
                        targets.AddRange(page.Results.Where(a => a.ID > 0).Select(a => new LocalAnimeTarget(a.ID, a.Title)));
                        choices.ItemsSource = targets.DistinctBy(a => a.AnimeId).ToArray();
                        choices.SelectedItem = selected;
                        if (selected is null) choices.SelectedIndex = 0;
                        assigned.IsChecked = true;
                    }
                    catch (OperationCanceledException) when (request.IsCancellationRequested) { }
                    catch (Exception error) when (error is HttpRequestException or Exceptions.MetadataServiceException or IOException)
                    { if (!request.IsCancellationRequested) ReportReadFailure(error); }
                    finally { search.IsEnabled = true; }
                };
                var title = new TextBox { Header = "显示标题", Text = item.AnimeTitle ?? "" };
                var episode = new NumberBox { Header = "集数（可选）", Minimum = 1, Value = item.EpisodeNumber ?? double.NaN,
                    Visibility = _playbackLauncher.IsAvailable ? Visibility.Visible : Visibility.Collapsed };
                var note = new TextBox { Header = "截图备注", Text = GetScreenshotNote(item), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
                try
                {
                    Task<bool>? saving = null;
                    await ShowEditorAsync("编辑截图", Fields(assigned, choices, query, search, title, episode, note), () => saving = SaveAsync(() =>
                    {
                        var target = assigned.IsChecked == true ? choices.SelectedItem as LocalAnimeTarget : null;
                        if (assigned.IsChecked == true && target is null) throw new ArgumentException("请选择关联作品。");
                        return _archive.UpdateScreenshotMetadataAsync(item.ScreenshotId, target?.AnimeId,
                            string.IsNullOrWhiteSpace(title.Text) ? target?.Title : title.Text,
                            _playbackLauncher.IsAvailable ? double.IsNaN(episode.Value) ? null : (int)episode.Value : item.EpisodeNumber, note.Text);
                    }));
                    if (saving is not null) await saving;
                }
                finally { request.Cancel(); }
            });
        }
        finally { _editingScreenshot = false; }
    }

    private async Task AddScreenshotTagsAsync(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return;
        var tags = new TextBox { Header = "个人标签（逗号分隔）" };
        await ShowEditorAsync(ids.Count == 1 ? "添加截图标签" : "批量添加标签", tags,
            () => SaveAsync(() => _archive.AddScreenshotTagsAsync(ids, SplitTags(tags.Text))), "添加");
    }
    private async void OnAddSelectedScreenshotTagClick(object sender, RoutedEventArgs e)
    { if (GetDetailScreenshot() is { } item) await AddScreenshotTagsAsync([item.ScreenshotId]); }
    private async void OnTagScreenshotsClick(object sender, RoutedEventArgs e)
        => await AddScreenshotTagsAsync(ScreenshotList.SelectedItems.OfType<AnimeScreenshot>().Select(a => a.ScreenshotId).ToArray());
    private async void OnRemoveScreenshotTagClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: string tag } button || GetDetailScreenshot() is not { } item || !button.IsEnabled) return;
        button.IsEnabled = false;
        try { await SaveAsync(() => _archive.RemoveScreenshotTagAsync(item.ScreenshotId, tag)); }
        finally { button.IsEnabled = true; }
    }

    private async void OnDeleteScreenshotClick(object sender, RoutedEventArgs e)
    {
        var selected = ScreenshotList.SelectedItems.OfType<AnimeScreenshot>().ToArray();
        if (selected.Length == 0 || _pageLifetime is not { } lifetime) return;
        if (!await ConfirmAsync("删除截图", $"将 {selected.Length} 张截图移入回收站？", "删除", lifetime.Token)) return;
        await SaveAsync(async () =>
        {
            var (deleted, failed) = await _screenshots.DeleteManyAsync(selected);
            if (ReferenceEquals(lifetime, _pageLifetime) && failed > 0)
                ShowStatus($"已删除 {deleted} 张，{failed} 张失败且保留记录。", InfoBarSeverity.Warning);
            else if (ReferenceEquals(lifetime, _pageLifetime)) ShowStatus($"{deleted} 张截图已移入回收站。", InfoBarSeverity.Success);
        }, success: null);
    }
    private async void OnCleanupScreenshotsClick(object sender, RoutedEventArgs e)
    {
        if (_pageLifetime is not { } lifetime || !await ConfirmAsync("清理缺失截图", "移除原图已缺失的截图记录和标签？", "清理", lifetime.Token)) return;
        await SaveAsync(async () => await _archive.RemoveMissingScreenshotRecordsAsync());
    }

    private async void OnExportScreenshotsClick(object sender, RoutedEventArgs e)
        => await ExportScreenshotItemsAsync(ScreenshotList.SelectedItems.OfType<AnimeScreenshot>().Where(a => a.FileExists).ToArray());
    private async void OnExportCurrentScreenshotClick(object sender, RoutedEventArgs e)
    { if (GetDetailScreenshot() is { FileExists: true } item) await ExportScreenshotItemsAsync([item]); }
    private Task ExportScreenshotItemsAsync(IReadOnlyList<AnimeScreenshot> selected) => ReadAsync(async () =>
    {
        if (selected.Count == 0 || _pageLifetime is not { } lifetime) return;
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
        if (!InitializePicker(picker)) return;
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null || lifetime.IsCancellationRequested) return;
        await SaveAsync(async () =>
        {
            var exported = 0;
            foreach (var item in selected)
            {
                try { var file = await StorageFile.GetFileFromPathAsync(item.FilePath); await file.CopyAsync(folder, file.Name, NameCollisionOption.GenerateUniqueName); exported++; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
                { _logger.LogWarning(error, "Screenshot copy failed"); }
            }
            if (ReferenceEquals(lifetime, _pageLifetime)) ShowStatus($"已复制 {exported} 张，{selected.Count - exported} 张未成功。",
                exported == selected.Count ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }, success: null);
    });

    private async void OnExportReviewClick(object sender, RoutedEventArgs e) => await ReadAsync(async () =>
    {
        if (_pageLifetime is not { } lifetime) return;
        var year = double.IsNaN(ReviewYear.Value) ? DateTime.Today.Year : (int)ReviewYear.Value;
        var playback = _playbackLauncher.IsAvailable;
        var picker = new FileSavePicker { SuggestedFileName = $"AniMeido-{year}-年度回顾" };
        picker.FileTypeChoices.Add("HTML", [".html"]);
        if (!InitializePicker(picker)) return;
        var file = await picker.PickSaveFileAsync();
        if (file is not null && !lifetime.IsCancellationRequested)
            await SaveAsync(() => _reviewExporter.ExportAsync(file.Path, year, playback));
    });

    private bool InitializePicker(object picker)
    {
        if (!_windowHandleProvider.TryGetWindowHandle(out var handle))
        { ShowStatus("文件选择窗口暂时无法打开，请稍后重试。", InfoBarSeverity.Warning); return false; }
        InitializeWithWindow.Initialize(picker, handle); return true;
    }

    private bool _editingScreenshot;

    private async void OnScreenshotSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_pageLifetime is not { } lifetime || _dialog is not null || _editingScreenshot) return;
        _editingScreenshot = true;
        try
        {
            await ReadAsync(async () =>
            {
                var settings = await ReadScreenshotSettingsAsync(lifetime.Token);
                if (!ReferenceEquals(lifetime, _pageLifetime)) return;
                var enabled = new ToggleSwitch { Header = "F12 全局截图", IsOn = settings.Enabled };
                var sound = new ToggleSwitch { Header = "截图音效", IsOn = settings.SoundEnabled };
                var popup = new ToggleSwitch { Header = "缩略图弹窗", IsOn = settings.PopupEnabled };
                var path = new TextBox { Header = "新截图保存目录", Text = settings.RootDirectory };
                Task<bool>? saving = null;
                await ShowEditorAsync("截图设置", Fields(enabled, sound, popup, path), () => saving = SaveAsync(async () =>
                {
                    if (string.IsNullOrWhiteSpace(path.Text)) throw new ArgumentException("请填写截图保存目录。");
                    var saved = new ScreenshotSettings(enabled.IsOn,
                        Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Text.Trim())), sound.IsOn, popup.IsOn);
                    await _archive.SaveScreenshotSettingsAsync(saved, CancellationToken.None);
                    _shortcut.SetEnabled(saved.Enabled);
                }));
                if (saving is not null) await saving;
            });
        }
        finally { _editingScreenshot = false; }
    }
}
