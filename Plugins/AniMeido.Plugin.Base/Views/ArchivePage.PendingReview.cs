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
        foreach (var state in _panelStates.Values) state.Invalidate();
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
            PendingReviewBanner.Content = _pendingReview.Pending.Count > 0
                ? $"有 {_pendingReview.Pending.Count} 部作品的记录需要确认" : "查看已保留记录";
            PendingReviewBanner.Visibility = _pendingReview.Pending.Count + _pendingReview.Kept.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (PendingReviewBanner.Visibility == Visibility.Collapsed) PendingReviewPanel.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Failed to load pending personal records");
            if (ReferenceEquals(_pageLifetime, lifetime)) ShowStatus("无法读取待确认记录，请刷新重试。", InfoBarSeverity.Warning);
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

    private async void OnPendingRebindClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PendingReviewItem item })
            await RunPendingActionAsync(ct => SelectPendingTargetAsync(item, false, ct));
    }

    private async void OnPendingMergeClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PendingReviewItem item })
            await RunPendingActionAsync(ct => SelectPendingTargetAsync(item, true, ct));
    }

    private async Task RunPendingActionAsync(Func<CancellationToken, Task> action)
    {
        if (_pendingActionRunning || _pageLifetime is null) return;
        _pendingActionRunning = true;
        var lifetime = _pageLifetime;
        PendingReviewPanel.IsEnabled = false;
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
            if (ReferenceEquals(_pageLifetime, lifetime)) PendingReviewPanel.IsEnabled = true;
        }
    }

    private async Task SelectPendingTargetAsync(PendingReviewItem item, bool localOnly, CancellationToken ct)
    {
        var search = new TextBox { PlaceholderText = localOnly ? "搜索本地已有记录的作品" : "搜索公共作品" };
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
            Title = localOnly ? "并入已有记录" : "改绑到公共作品",
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
                message.Text = publicResults.Count == 0 ? "没有找到作品；未更改任何记录。" : $"已加载 {publicResults.Count} 部作品";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) when (ex is HttpRequestException or Exceptions.MetadataServiceException or IOException or OperationCanceledException)
            {
                _logger.LogWarning(ex, "Pending record target search failed");
                if (!token.IsCancellationRequested) message.Text = "搜索失败，请重试；未更改任何记录。";
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
        var targets = await _pendingReview.GetTargetsAsync(item.AnimeId, ct);
        var merging = targets.Any(t => t.AnimeId == target.AnimeId);
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = merging ? "所选作品已有记录，将合并" : "确认改绑",
            Content = merging
                ? $"将〈{item.Title}〉并入〈{target.Title}〉。同时有追番状态、计划、播放进度时，较旧的那份会被丢弃（对应计划提醒也会删除）；感想、截图和观看记录会保留，档案笔记会拼接。"
                : $"将〈{item.Title}〉的全部个人记录改绑到〈{target.Title}〉，不丢弃记录。",
            PrimaryButtonText = merging ? "确认合并" : "确认改绑",
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
