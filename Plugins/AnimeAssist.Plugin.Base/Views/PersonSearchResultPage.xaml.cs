using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class PersonSearchResultPage : Page, INavigationAware
    {
        private readonly IAnimeDataSource _dataSource;
        private readonly TrackingService _tracking;
        private readonly IPluginNavigator _pluginNavigator;
        private CancellationTokenSource? _loadCts;

        public PersonSearchResultPage(IAnimeDataSource dataSource, TrackingService tracking, IPluginNavigator pluginNavigator)
        {
            _dataSource = dataSource;
            _tracking = tracking;
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

                var blocked = (await _tracking.GetAnimeIdsByStatusAsync(AnimeTrackingStatus.Blocked)).ToHashSet();
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

        private void OnResultItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Anime anime)
                _pluginNavigator.Navigate(typeof(AnimeDetailPage), anime.ID);
        }
    }
}
