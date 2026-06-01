using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class PersonSearchResultPage : Page
    {
        private IAnimeDataSource? _dataSource;

        public PersonSearchResultPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is (int personId, string personName))
            {
                TitleBlock.Text = $"声优作品：{personName}";
                _dataSource = AppServices.Provider?.GetRequiredService<IAnimeDataSource>();
                await LoadAsync(personId);
            }
        }

        private async Task LoadAsync(int personId)
        {
            if (_dataSource == null) return;

            LoadingOverlay.Visibility = Visibility.Visible;
            LoadingRing.IsActive = true;

            try
            {
                var works = await _dataSource.GetPersonWorksAsync(personId, CancellationToken.None);
                ResultCount.Text = $"参与 {works.Count} 部作品";

                // 并行获取每个作品的详细信息（最多同时 10 个请求）
                var semaphore = new SemaphoreSlim(10);
                var tasks = works
                    .Where(w => w.ID > 0)
                    .Select(async w =>
                    {
                        await semaphore.WaitAsync();
                        try
                        {
                            var detail = await _dataSource.GetAnimeDetailAsync(w.ID, CancellationToken.None);
                            return detail;
                        }
                        catch
                        {
                            // 单个作品获取失败时，用基础信息创建 Anime
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

                ResultGrid.ItemsSource = animes;

                if (animes.Count == 0)
                {
                    ResultCount.Text = "未找到相关作品";
                }
            }
            catch (Exception ex)
            {
                ResultCount.Text = $"加载失败：{ex.Message}";
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
                Frame.Navigate(typeof(AnimeDetailPage), anime.ID);
        }
    }
}
