using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class GlobalSearchPage : Page
    {
        private IAnimeDataSource? _dataSource;
        private string? _currentKeyword;
        private int _currentOffset;
        private int _totalResults;
        private const int PageSize = 20;

        public GlobalSearchPage()
        {
            InitializeComponent();
        }

        private void OnSearchClick(object sender, RoutedEventArgs e)
        {
            StartSearch();
        }

        private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
                StartSearch();
        }

        private void StartSearch()
        {
            var keyword = SearchBox.Text?.Trim();
            if (string.IsNullOrEmpty(keyword)) return;

            _currentKeyword = keyword;
            _currentOffset = 0;
            _ = SearchAsync(0);
        }

        private async Task SearchAsync(int offset)
        {
            if (_dataSource == null || _currentKeyword == null)
            {
                _dataSource = AppServices.Provider?.GetRequiredService<IAnimeDataSource>();
                if (_dataSource == null) return;
            }

            LoadingOverlay.Visibility = Visibility.Visible;
            LoadingRing.IsActive = true;
            PrevButton.IsEnabled = false;
            NextButton.IsEnabled = false;

            try
            {
                var (results, total) = await _dataSource.SearchByKeywordAsync(_currentKeyword, offset, CancellationToken.None);

                _currentOffset = offset;
                _totalResults = total;

                ResultGrid.ItemsSource = results;

                var currentPage = (offset / PageSize) + 1;
                var totalPages = Math.Max(1, (int)Math.Ceiling((double)total / PageSize));
                var totalDisplay = total >= 1000 ? $"{total}+" : total.ToString();
                ResultCount.Text = $"找到 {totalDisplay} 部番剧 · 第 {currentPage}/{totalPages} 页";
                PageInfo.Text = $"{currentPage} / {totalPages}";

                PrevButton.IsEnabled = offset > 0;
                NextButton.IsEnabled = (offset + PageSize) < total;
                PaginationBar.Visibility = Visibility.Visible;

                if (results.Count == 0)
                {
                    ResultCount.Text = "未找到相关番剧";
                    PaginationBar.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                ResultCount.Text = $"搜索失败：{ex.Message}";
                PaginationBar.Visibility = Visibility.Collapsed;
            }
            finally
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                LoadingRing.IsActive = false;
            }
        }

        private void OnPrevPage(object sender, RoutedEventArgs e)
        {
            var newOffset = _currentOffset - PageSize;
            if (newOffset >= 0)
                _ = SearchAsync(newOffset);
        }

        private void OnNextPage(object sender, RoutedEventArgs e)
        {
            var newOffset = _currentOffset + PageSize;
            if (newOffset < _totalResults)
                _ = SearchAsync(newOffset);
        }

        private void OnResultItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Anime anime)
                Frame.Navigate(typeof(AnimeDetailPage), anime.ID);
        }
    }
}
