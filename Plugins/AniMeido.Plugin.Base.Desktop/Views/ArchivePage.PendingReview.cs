using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class ArchivePage
{
    private bool _pendingActionRunning;

    private void OnPendingRecordsChanged()
    {
        var lifetime = _pageLifetime;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (lifetime is null || !ReferenceEquals(lifetime, _pageLifetime)) return;
            _panels.InvalidateAll(); _coverRequests.Clear();
            _ = ReadAsync(async () => await Task.WhenAll(RefreshPendingAsync(), EnsureActivePanelAsync()));
        });
    }

    private async Task RefreshPendingAsync()
    {
        if (_pageLifetime is not { } lifetime) return;
        await _pendingReview.LoadAsync(lifetime.Token);
        if (!ReferenceEquals(lifetime, _pageLifetime)) return;
        PendingReviewList.ItemsSource = _pendingReview.Pending;
        KeptReviewList.ItemsSource = _pendingReview.Kept;
        KeptReviewGroup.Visibility = _pendingReview.Kept.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        KeptReviewGroup.Header = $"暂不处理（{_pendingReview.Kept.Count}）";
        PendingReviewBanner.Content = _pendingReview.Pending.Count > 0
            ? $"有 {_pendingReview.Pending.Count} 部作品需要确认，点此查看"
            : $"查看暂不处理的作品（{_pendingReview.Kept.Count}）";
        PendingReviewBanner.Visibility = _pendingReview.Pending.Count + _pendingReview.Kept.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (PendingReviewBanner.Visibility == Visibility.Collapsed) PendingReviewPanel.Visibility = Visibility.Collapsed;
        PendingReviewPanel.IsEnabled = !_pendingActionRunning;
    }

    private void OnTogglePendingClick(object sender, RoutedEventArgs e)
        => PendingReviewPanel.Visibility = PendingReviewPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    private async void OnPendingKeepClick(object sender, RoutedEventArgs e) => await SetPendingKeptAsync(sender, true);
    private async void OnPendingUnkeepClick(object sender, RoutedEventArgs e) => await SetPendingKeptAsync(sender, false);
    private async Task SetPendingKeptAsync(object sender, bool kept)
    {
        if (sender is Button { Tag: PendingReviewItem item })
            await RunPendingActionAsync(async _ => await SaveAsync(() => _pendingReview.SetKeptAsync(item, kept, CancellationToken.None)));
    }
    private async void OnPendingPrimaryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PendingReviewItem item }) return;
        await RunPendingActionAsync(async token =>
        {
            var target = item.HasKnownTarget
                ? new LocalAnimeTarget(item.MergedInto!.Value, item.MergedIntoTitle ?? "另一部作品")
                : await SelectPendingTargetAsync(item, token);
            if (target is null || token.IsCancellationRequested) return;
            var targets = await _pendingReview.GetTargetsAsync(item.AnimeId, token);
            var merging = targets.Any(a => a.AnimeId == target.AnimeId);
            if (await ConfirmAsync(merging ? $"合并到《{target.Title}》？" : $"把记录移到《{target.Title}》？",
                merging ? "观看记录、截图、感想和标签都会保留。笔记会接在一起。追番状态和计划保留最近更新的一份，观看进度按集合并。"
                    : $"《{item.Title}》的记录将移到《{target.Title}》下。", merging ? "合并" : "移过去", token))
                await SaveAsync(() => _pendingReview.ApplyAsync(item, target.AnimeId, true, merging, CancellationToken.None));
        });
    }

    private async Task RunPendingActionAsync(Func<CancellationToken, Task> action)
    {
        if (_pendingActionRunning || _pageLifetime is not { } lifetime) return;
        _pendingActionRunning = true; PendingReviewPanel.IsEnabled = false;
        try { await ReadAsync(() => action(lifetime.Token)); }
        finally
        {
            _pendingActionRunning = false;
            PendingReviewPanel.IsEnabled = _pageLifetime is not null;
        }
    }

    private async Task<LocalAnimeTarget?> SelectPendingTargetAsync(PendingReviewItem item, CancellationToken token)
    {
        var localOnly = item.PicksFromOwnRecords;
        var local = localOnly ? await _pendingReview.GetTargetsAsync(item.AnimeId, token) : [];
        if (token.IsCancellationRequested) return null;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
        var searchToken = request.Token;
        var input = new TextBox { PlaceholderText = "输入番剧名称" };
        var results = new ListView { Height = 280, DisplayMemberPath = "Title", ItemsSource = local };
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var search = new Button { Content = "搜索" };
        var more = new Button { Content = "加载更多", IsEnabled = false };
        var content = Fields(input, search, results, more, message);
        content.Width = Math.Min(520, Math.Max(260, (XamlRoot?.Size.Width ?? 600) - 160));
        var dialog = new ContentDialog { Title = localOnly ? "合并到记录过的作品" : "选择对应的番剧", Content = content,
            PrimaryButtonText = "选择", CloseButtonText = "取消", IsPrimaryButtonEnabled = false };
        results.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = results.SelectedItem is LocalAnimeTarget;
        var items = new List<Anime>();
        var offset = 0; var query = ""; var busy = false;
        async Task SearchAsync(bool next)
        {
            if (busy || searchToken.IsCancellationRequested) return;
            busy = true; search.IsEnabled = more.IsEnabled = false;
            try
            {
                if (localOnly)
                { results.ItemsSource = local.Where(a => a.Title.Contains(input.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToArray(); return; }
                if (!next) { query = input.Text.Trim(); offset = 0; items.Clear(); results.ItemsSource = null; }
                if (query.Length == 0) return;
                var page = await _pendingReview.SearchAsync(query, offset, searchToken);
                if (searchToken.IsCancellationRequested) return;
                var previous = offset;
                offset = page.NextOffset;
                items.AddRange(page.Results.Where(a => a.ID > 0 && a.ID != item.AnimeId));
                results.ItemsSource = items.DistinctBy(a => a.ID).Select(a => new LocalAnimeTarget(a.ID, a.Title)).ToArray();
                more.IsEnabled = offset > previous && offset < page.Total;
                message.Text = items.Count == 0 ? "没有找到，换个名称试试。" : $"找到 {items.DistinctBy(a => a.ID).Count()} 部";
            }
            catch (OperationCanceledException) when (searchToken.IsCancellationRequested) { }
            catch (Exception error) when (error is HttpRequestException or IOException or Exceptions.MetadataServiceException)
            {
                if (!searchToken.IsCancellationRequested)
                { _logger.LogWarning(error, "Archive target search failed"); message.Text = "搜索失败，请稍后重试。"; }
            }
            finally { busy = false; if (!searchToken.IsCancellationRequested) search.IsEnabled = true; }
        }
        search.Click += async (_, _) => await SearchAsync(false);
        more.Click += async (_, _) => await SearchAsync(true);
        try
        {
            var choice = await ShowDialogAsync(dialog, token);
            return choice == ContentDialogResult.Primary && !token.IsCancellationRequested ? results.SelectedItem as LocalAnimeTarget : null;
        }
        finally { request.Cancel(); }
    }
}
