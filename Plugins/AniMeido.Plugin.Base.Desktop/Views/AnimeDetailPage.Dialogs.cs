using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class AnimeDetailPage
{
    private void OnPersonalArchiveClick(object sender, RoutedEventArgs args)
    {
        if (_visit is null || _editor.IsEditing || ViewModel.AnimeDetail is not { } anime) return;
        var id = _currentAnimeId;
        var version = _navigationVersion;
        PersonalArchiveButton.IsEnabled = false;
        _ = Write(async token =>
        {
            bool IsCurrent() => !token.IsCancellationRequested && IsCurrentNavigation(version, id);
            try
            {
                await _editor.EditAsync(id, (existing, tags) => EditArchiveAsync(anime, existing, tags, version, token), IsCurrent, token);
            }
            finally { if (IsCurrent()) UpdateState(); }
        });
    }

    private async Task EditArchiveAsync(Anime anime, AnimeArchive? existing, IReadOnlyList<string> existingTags, int version, CancellationToken token)
    {
        if (token.IsCancellationRequested || !IsCurrentNavigation(version, anime.ID) || _dialog is not null) return;
        var rating = new NumberBox
        {
            Header = "个人评分（0.5–10，可留空）", Minimum = 0.5, Maximum = 10, SmallChange = 0.5,
            Value = existing?.PersonalRating ?? double.NaN, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        var tags = new TextBox { Header = "个人标签", PlaceholderText = "使用逗号分隔", Text = string.Join(", ", existingTags) };
        var note = new TextBox { Header = "概要笔记", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, Text = existing?.SummaryNote ?? "" };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(rating); panel.Children.Add(tags); panel.Children.Add(note);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = $"《{anime.Title}》个人档案", Content = panel,
            PrimaryButtonText = "保存", SecondaryButtonText = "打开档案馆", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary,
        };
        _dialog = dialog;
        try
        {
            var result = await dialog.ShowAsync();
            if (token.IsCancellationRequested || !IsCurrentNavigation(version, anime.ID)) return;
            if (result == ContentDialogResult.Secondary)
            {
                if (existing is null) await _archive.UpsertArchiveAsync(anime.ID, anime.Title, null, "");
                if (!token.IsCancellationRequested && IsCurrentNavigation(version, anime.ID)) _navigator.Navigate(typeof(ArchivePage), anime.ID);
            }
            else if (result == ContentDialogResult.Primary)
            {
                // 评分、笔记和标签由现有服务在同一个事务内保存。
                await _archive.SaveArchiveWithTagsAsync(anime.ID, anime.Title, double.IsNaN(rating.Value) ? null : rating.Value,
                    note.Text, tags.Text.Split([',', '，'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                if (!token.IsCancellationRequested && IsCurrentNavigation(version, anime.ID))
                    await RefreshDesktopArchiveSummaryAsync(anime.ID, version, token);
            }
        }
        finally { if (ReferenceEquals(_dialog, dialog)) _dialog = null; }
    }
}
