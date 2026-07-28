using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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

        private bool _dropHostRegistered;

        public PersonSearchResultPage(IAnimeDataSource dataSource, TrackingService tracking, DragDropService dragDropService, IPluginNavigator pluginNavigator)
        {
            _dataSource = dataSource;
            _tracking = tracking;
            _dragDrop = dragDropService;
            _pluginNavigator = pluginNavigator;
            InitializeComponent();
        }

        public async Task OnNavigatedToAsync(object? parameter)
        {
            // 取消上一轮加载
            _loadCts?.Cancel();
            _loadCts = new CancellationTokenSource();
            var token = _loadCts.Token;

            if (parameter is (int personId, string personName))
            {
                TitleBlock.Text = $"声优作品：{personName}";
                await LoadAsync(personId, token);
            }
        }

        private async Task LoadAsync(int personId, CancellationToken cancellationToken)
        {
            if (_dataSource == null) return;

            LoadingOverlay.Visibility = Visibility.Visible;
            LoadingRing.IsActive = true;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var works = await _dataSource.GetPersonWorksAsync(personId, cancellationToken);
                ResultCount.Text = $"参与 {works.Count} 部作品";

                // 并行获取每个作品的详细信息（最多同时 4 个请求）
                var semaphore = new SemaphoreSlim(4);
                var tasks = works
                    .Where(w => w.ID > 0)
                    .Select(async w =>
                    {
                        await semaphore.WaitAsync(cancellationToken);
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var detail = await _dataSource.GetAnimeDetailAsync(w.ID, cancellationToken);
                            return detail;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (HttpRequestException)
                        {
                            // 单个作品网络请求失败时，用基础信息创建 Anime
                            return new Anime(
                                w.ID, w.Title, null, Array.Empty<VoiceActor>(),
                                null, w.CoverURL, w.Staff ?? "", 0, 0, null, null);
                        }
                        catch (JsonException)
                        {
                            // 单个作品解析失败时，用基础信息创建 Anime
                            return new Anime(
                                w.ID, w.Title, null, Array.Empty<VoiceActor>(),
                                null, w.CoverURL, w.Staff ?? "", 0, 0, null, null);
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    });

                var animes = (await Task.WhenAll(tasks))
                    .Where(a => a != null)
                    .Cast<Anime>()
                    .ToList();

                if (cancellationToken.IsCancellationRequested) return;

                var blocked = await _tracking.GetBlockedAnimeIdsAsync();
                ResultGrid.ItemsSource = animes.Where(a => !blocked.Contains(a.ID)).ToList();

                if (animes.Count == 0)
                {
                    ResultCount.Text = "未找到相关作品";
                }
            }
            catch (OperationCanceledException)
            {
                // 页面已离开，不更新 UI
            }
            catch (HttpRequestException ex)
            {
                ResultCount.Text = $"加载失败：{ex.Message}";
            }
            catch (JsonException ex)
            {
                ResultCount.Text = $"数据解析失败：{ex.Message}";
            }
            finally
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                LoadingRing.IsActive = false;
            }
        }

        // ======== 拖放 ========

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Grid rootGrid)
                return;

            EnsureDropHostRegistered(rootGrid);

            rootGrid.Unloaded -= OnRootGridUnloaded;
            rootGrid.Unloaded += OnRootGridUnloaded;

            _ = RemoveBlockedResultsAsync();
        }

        private async Task RemoveBlockedResultsAsync()
        {
            try
            {
                var blocked = await _tracking.GetBlockedAnimeIdsAsync();
                if (ResultGrid.ItemsSource is IEnumerable<Anime> current)
                {
                    ResultGrid.ItemsSource = current
                        .Where(anime => !blocked.Contains(anime.ID))
                        .ToList();
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

        private void EnsureDropHostRegistered(Grid rootGrid)
        {
            if (_dropHostRegistered)
                return;
            _dropHostRegistered = true;

            _dragDrop.SetActiveDropContext(rootGrid, DragOverlay, DragAction.PlanToWatch);
            _dragDrop.RegisterStandardDragHost(rootGrid);
        }

        private void OnRootGridUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Grid rootGrid)
                return;

            _dragDrop.ClearActiveDropContext(rootGrid);
            _dragDrop.UnregisterStandardDragHost(rootGrid);
            _dropHostRegistered = false;
        }



        private void OnAnimeCardClicked(object? sender, Views.Controls.AnimeCardClickedEventArgs e)
        {
            _pluginNavigator.Navigate(typeof(AnimeDetailPage), e.Anime.ID);
        }
    }
}
