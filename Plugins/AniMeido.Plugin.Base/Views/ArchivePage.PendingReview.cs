using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class ArchivePage
{
    private bool _pendingSubscribed;
    private bool _pendingActionRunning;

    private void UpdatePendingPanelAvailability() => PendingReviewPanel.IsEnabled =
        _pageLifetime is { IsCancellationRequested: false } && !_pendingActionRunning;

    private void SubscribePendingChanges()
    {
        if (_pendingSubscribed) return;
        _idRemap.Changed += OnPendingRecordsChanged;
        _pendingSubscribed = true;
    }

    private void UnsubscribePendingChanges()
    {
        if (!_pendingSubscribed) return;
        _idRemap.Changed -= OnPendingRecordsChanged;
        _pendingSubscribed = false;
    }

    private void OnPendingRecordsChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_pageLifetime is null) return;
        _panels.InvalidateAll();
        _coverRequests.Clear();
        _ = RefreshPendingAsync();
        _ = EnsureActivePanelAsync();
    });

    private async Task RefreshPendingAsync()
    {
        var lifetime = _pageLifetime;
        if (lifetime is null) return;
        try
        {
            await _pendingReview.LoadAsync(lifetime.Token);
            if (!ReferenceEquals(_pageLifetime, lifetime) || lifetime.IsCancellationRequested) return;
            PendingReviewList.ItemsSource = _pendingReview.Pending;
            KeptReviewList.ItemsSource = _pendingReview.Kept;
            KeptReviewGroup.Visibility = _pendingReview.Kept.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            KeptReviewGroup.Header = $"暂不处理（{_pendingReview.Kept.Count}）";
            PendingReviewBanner.Content = _pendingReview.Pending.Count > 0
                ? $"有 {_pendingReview.Pending.Count} 部作品没能对上番剧资料，点此查看"
                : $"查看暂不处理的作品（{_pendingReview.Kept.Count}）";
            PendingReviewBanner.Visibility = _pendingReview.Pending.Count + _pendingReview.Kept.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (PendingReviewBanner.Visibility == Visibility.Collapsed) PendingReviewPanel.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Failed to load pending personal records");
            if (ReferenceEquals(_pageLifetime, lifetime)) ShowStatus("没能读取需要确认的作品，点右上角“刷新”再试一次。", InfoBarSeverity.Warning);
        }
    }

    private void OnTogglePendingClick(object sender, RoutedEventArgs e) => PendingReviewPanel.Visibility =
        PendingReviewPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private async void OnPendingKeepClick(object sender, RoutedEventArgs e) => await SetPendingKeptAsync(sender, true);
    private async void OnPendingUnkeepClick(object sender, RoutedEventArgs e) => await SetPendingKeptAsync(sender, false);

    private async Task SetPendingKeptAsync(object sender, bool kept)
    {
        if (sender is not Button { Tag: PendingReviewItem item }) return;
        await RunPendingActionAsync(async ct => { await _pendingReview.SetKeptAsync(item, kept, ct); await RefreshPendingAsync(); });
    }

    private async void OnPendingPrimaryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PendingReviewItem item }) return;
        await RunPendingActionAsync(ct => item.HasKnownTarget
            ? ConfirmPendingTargetAsync(item, new LocalAnimeTarget(item.MergedInto!.Value, item.MergedIntoTitle ?? "另一部作品"), ct)
            : SelectPendingTargetAsync(item, item.PicksFromOwnRecords, ct));
    }

    private async Task RunPendingActionAsync(Func<CancellationToken, Task> action)
    {
        if (_pendingActionRunning || _pageLifetime is null) return;
        _pendingActionRunning = true;
        var lifetime = _pageLifetime;
        UpdatePendingPanelAvailability();
        try { await action(lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException
            or HttpRequestException or Exceptions.MetadataServiceException or ArgumentException)
        {
            _logger.LogWarning(ex, "Pending personal record action failed");
            if (ReferenceEquals(_pageLifetime, lifetime)) ShowStatus(ex.Message, InfoBarSeverity.Warning);
        }
        finally
        {
            _pendingActionRunning = false;
            UpdatePendingPanelAvailability();
        }
    }

    private async Task SelectPendingTargetAsync(PendingReviewItem item, bool localOnly, CancellationToken ct)
    {
        var search = new TextBox { PlaceholderText = localOnly ? "输入番剧名称筛选" : "输入番剧名称" };
        var results = new ListView { Height = 280, SelectionMode = ListViewSelectionMode.Single };
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var searchButton = new Button { Content = "搜索" };
        var moreButton = new Button { Content = "加载更多", IsEnabled = false };
        var controls = new StackPanel { Spacing = 8, Width = 520 };
        controls.Children.Add(search); controls.Children.Add(searchButton); controls.Children.Add(results);
        controls.Children.Add(moreButton); controls.Children.Add(message);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = localOnly ? "合并到你记录过的另一部作品" : "选择对应的番剧",
            Content = controls,
            PrimaryButtonText = "选择",
            CloseButtonText = "取消",
            IsPrimaryButtonEnabled = false
        };
        using var request = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = request.Token;
        var local = localOnly ? await _pendingReview.GetTargetsAsync(item.AnimeId, ct) : [];
        var publicResults = new List<Anime>();
        var offset = 0;
        var query = "";
        var searching = false;
        async Task Search(bool more)
        {
            if (searching) return;
            searching = true; searchButton.IsEnabled = false; moreButton.IsEnabled = false;
            try
            {
                if (localOnly)
                {
                    results.ItemsSource = local.Where(t => t.Title.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)
                        || t.AnimeId.ToString().Contains(search.Text.Trim(), StringComparison.Ordinal)).ToList();
                    return;
                }
                if (!more) { query = search.Text.Trim(); offset = 0; publicResults.Clear(); }
                if (query.Length == 0) { results.ItemsSource = null; return; }
                var page = await _pendingReview.SearchAsync(query, offset, token);
                if (token.IsCancellationRequested) return;
                offset = page.NextOffset;
                publicResults.AddRange(page.Results.Where(a => a.ID > 0 && a.ID != item.AnimeId));
                results.ItemsSource = publicResults.DistinctBy(a => a.ID).Select(a => new LocalAnimeTarget(a.ID, a.Title)).ToList();
                moreButton.IsEnabled = offset < page.Total;
                message.Text = publicResults.Count == 0 ? "没有找到，换个名称试试。" : $"找到 {publicResults.Count} 部";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) when (ex is HttpRequestException or Exceptions.MetadataServiceException or IOException or OperationCanceledException)
            {
                if (!token.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Pending record target search failed");
                    message.Text = "搜索失败，请稍后再试。";
                }
            }
            finally { searching = false; if (!token.IsCancellationRequested) searchButton.IsEnabled = true; }
        }
        searchButton.Click += async (_, _) => await Search(false);
        moreButton.Click += async (_, _) => await Search(true);
        results.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = results.SelectedItem is LocalAnimeTarget;
        if (localOnly)
        {
            results.ItemsSource = local;
            results.SelectedItem = local.FirstOrDefault(t => t.AnimeId == item.MergedInto);
        }
        using var cancel = ct.Register(() => DispatcherQueue.TryEnqueue(dialog.Hide));
        var choice = await dialog.ShowAsync();
        request.Cancel();
        if (choice != ContentDialogResult.Primary || results.SelectedItem is not LocalAnimeTarget target || ct.IsCancellationRequested) return;
        await ConfirmPendingTargetAsync(item, target, ct);
    }

    private async Task ConfirmPendingTargetAsync(PendingReviewItem item, LocalAnimeTarget target, CancellationToken ct)
    {
        var targets = await _pendingReview.GetTargetsAsync(item.AnimeId, ct);
        var merging = targets.Any(t => t.AnimeId == target.AnimeId);
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = merging ? $"合并到《{target.Title}》？" : $"把记录移到《{target.Title}》？",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = merging
                    ? "两边的观看记录、截图、感想和个人标签都会保留，档案笔记会接在一起。追番状态和计划只能留一份，会保留最近更新的那份；观看进度按集合并。"
                    : $"《{item.Title}》的所有记录都会移到《{target.Title}》下，不会丢失任何内容。"
            },
            PrimaryButtonText = merging ? "合并" : "移过去",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        using var cancelConfirm = ct.Register(() => DispatcherQueue.TryEnqueue(confirm.Hide));
        var confirmed = await confirm.ShowAsync() == ContentDialogResult.Primary;
        if (ct.IsCancellationRequested) return;
        await _pendingReview.ApplyAsync(item, target.AnimeId, confirmed, merging, ct);
        if (confirmed) await RefreshPendingAsync();
    }
}
