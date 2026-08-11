using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class PersonSearchResultPage : Page, INavigationAware
    {
        private readonly IAnimeDataSource _dataSource;
        private readonly TrackingService _tracking;
        private readonly IPluginNavigator _pluginNavigator;
        private readonly DragDropService _dragDrop;
        private CancellationTokenSource? _loadCts;
        private readonly ObservableCollection<Anime> _results = [];
        private int _loadVersion;

        private IDisposable? _dropHostRegistration;

        public PersonSearchResultPage(IAnimeDataSource dataSource, TrackingService tracking, DragDropService dragDropService, IPluginNavigator pluginNavigator)
        {
            _dataSource = dataSource;
            _tracking = tracking;
            _dragDrop = dragDropService;
            _pluginNavigator = pluginNavigator;
            InitializeComponent();
            ResultGrid.ItemsSource = _results;
        }

        public async Task OnNavigatedToAsync(object? parameter)
        {
            // 取消上一轮加载
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = new CancellationTokenSource();
            var token = _loadCts.Token;
            var loadVersion = Interlocked.Increment(ref _loadVersion);

            if (parameter is (int personId, string personName))
            {
                TitleBlock.Text = $"声优作品：{personName}";
                await LoadAsync(personId, loadVersion, token);
            }
        }

        private async Task LoadAsync(
            int personId,
            int loadVersion,
            CancellationToken cancellationToken)
        {
            if (_dataSource == null) return;

            LoadingOverlay.Visibility = Visibility.Visible;
            LoadingRing.IsActive = true;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var works = await _dataSource.GetPersonWorksAsync(personId, cancellationToken);
                var visibleWorks = works
                    .Where(work => work.ID > 0)
                    .DistinctBy(work => work.ID)
                    .ToList();
                var blocked = await _tracking.GetBlockedAnimeIdsAsync();
                if (!IsCurrentLoad(loadVersion, cancellationToken))
                    return;

                _results.Clear();
                LoadingOverlay.Visibility = Visibility.Collapsed;
                LoadingRing.IsActive = false;
                ResultCount.Text = $"正在加载 0/{visibleWorks.Count} 部作品";
                var processedCount = 0;
                foreach (var batch in visibleWorks.Chunk(4))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var loaded = await Task.WhenAll(batch.Select(work =>
                        LoadWorkAsync(work, cancellationToken)));
                    if (!IsCurrentLoad(loadVersion, cancellationToken))
                        return;

                    foreach (var anime in loaded)
                    {
                        if (anime is not null && !blocked.Contains(anime.ID))
                            _results.Add(anime);
                    }

                    processedCount += batch.Length;
                    ResultCount.Text =
                        $"正在加载 {processedCount}/{visibleWorks.Count} 部作品";
                }

                ResultCount.Text = _results.Count == 0
                    ? "未找到相关作品"
                    : $"参与 {visibleWorks.Count} 部作品 · 显示 {_results.Count} 部";
            }
            catch (OperationCanceledException)
            {
                // 页面已离开，不更新 UI
            }
            catch (HttpRequestException ex)
            {
                ResultCount.Text = $"加载失败：{ex.Message}";
            }
            catch (BangumiApiException ex)
            {
                ResultCount.Text = $"数据源请求失败：{ex.Message}";
            }
            catch (JsonException ex)
            {
                ResultCount.Text = $"数据解析失败：{ex.Message}";
            }
            finally
            {
                if (loadVersion == _loadVersion)
                {
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                    LoadingRing.IsActive = false;
                }
            }
        }

        private async Task<Anime?> LoadWorkAsync(
            PersonWork work,
            CancellationToken cancellationToken)
        {
            try
            {
                return await _dataSource.GetAnimeDetailAsync(
                    work.ID,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (
                ex is HttpRequestException
                or BangumiApiException
                or JsonException)
            {
                return new Anime(
                    work.ID,
                    work.Title,
                    null,
                    [],
                    null,
                    work.CoverURL,
                    work.Staff ?? string.Empty,
                    0,
                    0,
                    null,
                    null);
            }
        }

        private bool IsCurrentLoad(
            int loadVersion,
            CancellationToken cancellationToken)
            => !cancellationToken.IsCancellationRequested
                && loadVersion == _loadVersion;

        // ======== 拖放 ========

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Grid rootGrid)
                return;

            _dropHostRegistration?.Dispose();
            _dropHostRegistration = _dragDrop.AttachStandardDragHost(
                rootGrid,
                DragOverlay,
                DragAction.PlanToWatch);

            rootGrid.Unloaded -= OnRootGridUnloaded;
            rootGrid.Unloaded += OnRootGridUnloaded;

            _ = RemoveBlockedResultsAsync();
        }

        private async Task RemoveBlockedResultsAsync()
        {
            try
            {
                var blocked = await _tracking.GetBlockedAnimeIdsAsync();
                for (var index = _results.Count - 1; index >= 0; index--)
                {
                    if (blocked.Contains(_results[index].ID))
                        _results.RemoveAt(index);
                }
            }
#pragma warning disable CA1031 // 可见性刷新失败不应清空已有结果
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[PersonSearchResultPage] RemoveBlockedResultsAsync failed: {ex.Message}");
            }
#pragma warning restore CA1031
        }

        private void OnRootGridUnloaded(object sender, RoutedEventArgs e)
        {
            Interlocked.Increment(ref _loadVersion);
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = null;
            _dropHostRegistration?.Dispose();
            _dropHostRegistration = null;
        }



        private void OnAnimeCardClicked(object? sender, Views.Controls.AnimeCardClickedEventArgs e)
        {
            _pluginNavigator.Navigate(typeof(AnimeDetailPage), e.Anime.ID);
        }
    }
}
