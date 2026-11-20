using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class ArchivePage
{
    private Task<bool> SaveAsync(Func<Task> persist, string? success = "已保存。")
    {
        if (_pageLifetime is not { } lifetime) return Task.FromResult(false);
        var task = SaveCoreAsync();
        _operations.Track(task);
        return task;
        async Task<bool> SaveCoreAsync()
        {
            try { await persist(); }
            catch (ArgumentException error)
            { if (ReferenceEquals(lifetime, _pageLifetime)) ShowStatus(error.Message, InfoBarSeverity.Warning); return false; }
            catch (PersonalDataBusyException error)
            { if (ReferenceEquals(lifetime, _pageLifetime)) ShowStatus(error.Message, InfoBarSeverity.Warning); return false; }
            catch (Exception error) when (error is SqliteException or IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                _logger.LogError(error, "Archive operation failed");
                if (ReferenceEquals(lifetime, _pageLifetime)) ShowStatus("操作未完成，请稍后重试。", InfoBarSeverity.Error);
                return false;
            }
            _panels.InvalidateAll();
            if (ReferenceEquals(lifetime, _pageLifetime))
            {
                await ReadAsync(async () => await Task.WhenAll(EnsureActivePanelAsync(), RefreshPendingAsync()));
                if (success is not null && ReferenceEquals(lifetime, _pageLifetime)) ShowStatus(success, InfoBarSeverity.Success);
            }
            return true;
        }
    }

    internal Task<bool> SaveArchiveAsync(int id, string title, double? rating, string note, IEnumerable<string> tags)
        => SaveAsync(() => _archive.SaveArchiveWithTagsAsync(id, title, rating, note, tags, CancellationToken.None));

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog, CancellationToken token)
    {
        if (_dialog is not null || token.IsCancellationRequested || XamlRoot is null) return ContentDialogResult.None;
        dialog.XamlRoot = XamlRoot;
        _dialog = dialog;
        using var cancel = token.Register(() => DispatcherQueue.TryEnqueue(dialog.Hide));
        try { return await dialog.ShowAsync(); }
        finally { if (ReferenceEquals(_dialog, dialog)) _dialog = null; }
    }

    private async Task ShowEditorAsync(string title, object content, Func<Task<bool>> save, string primary = "保存")
    {
        if (_pageLifetime is not { } lifetime || _dialog is not null) return;
        var dialog = new ContentDialog { Title = title, Content = content, PrimaryButtonText = primary,
            CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        if (content is FrameworkElement { Width: > 0 } element && double.IsFinite(element.Width))
            dialog.Resources["ContentDialogMaxWidth"] = element.Width + 64;
        var busy = false;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (busy || !ReferenceEquals(lifetime, _pageLifetime)) { args.Cancel = true; return; }
            busy = true;
            var deferral = args.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            try { args.Cancel = !await save(); }
            finally { busy = false; dialog.IsPrimaryButtonEnabled = true; deferral.Complete(); }
        };
        await ShowDialogAsync(dialog, lifetime.Token);
    }

    private async Task<bool> ConfirmAsync(string title, string text, string primary, CancellationToken token)
        => await ShowDialogAsync(new ContentDialog { Title = title,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary, CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close }, token)
            == ContentDialogResult.Primary && !token.IsCancellationRequested;

    private static StackPanel Fields(params UIElement[] fields)
    {
        var panel = new StackPanel { Spacing = 12 };
        foreach (var field in fields) panel.Children.Add(field);
        return panel;
    }

    private async void OnEditArchiveClick(object sender, RoutedEventArgs e)
    {
        if (_details.SelectedArchive is not { } selected) return;
        var archive = selected.Archive;
        var rating = new NumberBox { Header = "个人评分", Minimum = 0.5, Maximum = 10, SmallChange = 0.5,
            Value = archive.PersonalRating ?? double.NaN };
        var tags = new TextBox { Header = "个人标签（逗号分隔）", Text = string.Join(", ", selected.Tags) };
        await ShowEditorAsync("编辑档案", Fields(rating, tags),
            () => SaveArchiveAsync(archive.AnimeId, archive.TitleSnapshot,
                double.IsNaN(rating.Value) ? null : rating.Value, archive.SummaryNote, SplitTags(tags.Text)));
    }

    private async void OnEditNoteClick(object sender, RoutedEventArgs e)
    {
        if (_details.SelectedArchive is not { } selected || XamlRoot is not { } root || _dialog is not null) return;
        var archive = selected.Archive;
        var width = Math.Min(1000, Math.Max(320, root.Size.Width - 160));
        var split = width >= 760;
        var height = Math.Clamp(root.Size.Height - 280, 200, 500) / (split ? 1 : 2);
        var source = new TextBox { Header = "Markdown", Text = archive.SummaryNote, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, Height = height };
        var preview = new RichTextBlock { TextWrapping = TextWrapping.Wrap };
        ArchiveMarkdownRenderer.Render(preview, source.Text);
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(150); timer.IsRepeating = false;
        timer.Tick += (_, _) => ArchiveMarkdownRenderer.Render(preview, source.Text);
        source.TextChanged += (_, _) => { timer.Stop(); timer.Start(); };
        var pane = Fields(new TextBlock { Text = "实时预览" }, new ScrollViewer { Content = preview, Height = height });
        var layout = new Grid { Width = width, ColumnSpacing = 18, RowSpacing = 12 };
        layout.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        if (split) { layout.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); Grid.SetColumn(pane, 1); }
        else { layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); Grid.SetRow(pane, 1); }
        layout.Children.Add(source); layout.Children.Add(pane);
        try
        {
            await ShowEditorAsync("编辑作品笔记", layout,
                () => SaveAsync(() => _archive.UpsertArchiveAsync(archive.AnimeId, archive.TitleSnapshot,
                    archive.PersonalRating, source.Text, CancellationToken.None)));
        }
        finally { timer.Stop(); }
    }

    private async void OnAddEntryClick(object sender, RoutedEventArgs e)
    {
        if (_details.SelectedArchive is { } selected) await EditEntryAsync(selected.Archive.AnimeId, null);
    }
    private async void OnEditEntryClick(object sender, RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is ArchiveTimelineItem { Entry: { } entry }) await EditEntryAsync(entry.AnimeId, entry);
    }
    private async Task EditEntryAsync(int id, ArchiveEntry? entry)
    {
        var body = new TextBox { Header = "观看感想", Text = entry?.Body ?? "", AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, MinHeight = 120 };
        var episode = new NumberBox { Header = "集数（可选）", Minimum = 1, Value = entry?.EpisodeNumber ?? double.NaN };
        await ShowEditorAsync(entry is null ? "追加观看感想" : "编辑观看感想", Fields(episode, body),
            () => SaveAsync(() => entry is null
                ? _archive.AddEntryAsync(id, DateTimeOffset.Now, double.IsNaN(episode.Value) ? null : (int)episode.Value, body.Text)
                : _archive.UpdateEntryAsync(entry.EntryId, entry.OccurredAt,
                    double.IsNaN(episode.Value) ? null : (int)episode.Value, body.Text)));
    }
    private async void OnDeleteEntryClick(object sender, RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is not ArchiveTimelineItem { Entry: { } entry } || _pageLifetime is not { } lifetime) return;
        if (await ConfirmAsync("删除观看感想", "这条感想将永久删除。", "删除", lifetime.Token))
            await SaveAsync(() => _archive.DeleteEntryAsync(entry.EntryId));
    }

    private async void OnAddManualEventClick(object sender, RoutedEventArgs e)
    {
        if (_details.SelectedArchive is not { } selected) return;
        var archive = selected.Archive;
        var date = new CalendarDatePicker { Header = "观看日期", Date = DateTimeOffset.Now };
        var time = new TimePicker { Header = "观看时间", Time = DateTimeOffset.Now.TimeOfDay };
        var from = new NumberBox { Header = "起始集", Minimum = 1, Value = 1 };
        var to = new NumberBox { Header = "结束集", Minimum = 1, Value = 1 };
        var minutes = new NumberBox { Header = "观看分钟数（可选）", Minimum = 1 };
        var note = new TextBox { Header = "备注" };
        await ShowEditorAsync("补录观看事件", Fields(date, time, from, to, minutes, note), () => SaveAsync(async () =>
        {
            if (date.Date is null) throw new ArgumentException("请选择观看日期。");
            var local = date.Date.Value.Date.Add(time.Time);
            await _archive.AddManualWatchEventAsync(new(Guid.NewGuid().ToString("N"), archive.AnimeId, archive.TitleSnapshot,
                new(local, TimeZoneInfo.Local.GetUtcOffset(local)), (int)from.Value, (int)to.Value,
                double.IsNaN(minutes.Value) ? null : (int)minutes.Value, note.Text, DateTimeOffset.UtcNow));
        }));
    }
}
